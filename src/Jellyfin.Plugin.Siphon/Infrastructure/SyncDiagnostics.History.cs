using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Siphon.Infrastructure;

public sealed partial class SyncDiagnostics
{
    private const int MaximumDecisions = 100000;
    private const long MaximumDecisionFileBytes = 256L * 1024 * 1024;
    private static readonly Regex ContentKeyPattern = new(@"\A(?:(?:movie|series):(?:tt[0-9]{7,10}|(?:tmdb|tvdb|mal):[0-9]{1,64}|opaque:[A-Fa-f0-9]{64})|redacted:[A-F0-9]{64})\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex SecretTextPattern = new(@"(?i)(?:[a-z][a-z0-9+.-]*://|www\.|[?&=]|%[0-9a-f]{2}|\b(?:bearer|(?:access[_-]?)?token|password|secret|api[_-]?key)\b|[a-z0-9_-]{40,})", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly HashSet<string> Actions = new(StringComparer.Ordinal) { "Pending", "Added", "Updated", "Unchanged", "Preserved", "Removed", "Error", "ProviderPaused" };
    private static readonly HashSet<string> DecisionReasons = new(StringComparer.Ordinal)
    {
        "Scheduled", "Discovered", "SeriesRefresh", "IncompleteCatalog", "CatalogUnavailable", "MetadataUnavailable", "NoChanges", "Published",
        "CleanupRetention", "ConfirmedAbsence", "ItemsRemoved", "MetadataLocked", "ProviderPaused", "ProviderError", "CacheHit", "Cancelled", "NotPublished", "Interrupted"
    };
    private readonly List<SyncRunSnapshot> _history = [];
    private readonly AsyncLocal<(string Key, string Name)?> _title = new();
    private RunState? _finishedDecisions;

    public IReadOnlyList<SyncRunSnapshot> GetHistory()
    {
        lock (_gate)
            return (_currentRun is { } current ? _history.Prepend(current.Snapshot()) : _history).Take(20).ToArray();
    }

    public SyncDecisionPage? GetHistoryDetails(string id, int startIndex, int limit)
    {
        if (!Guid.TryParseExact(id, "N", out _)) return null;
        if (startIndex < 0 || startIndex > MaximumDecisions || limit is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(startIndex), "Use a startIndex from 0 to 100000 and a limit from 1 to 200.");
        lock (_gate)
        {
            var run = _currentRun?.Id == id ? _currentRun.Snapshot() : _history.FirstOrDefault(item => item.Id == id);
            if (run is null) return null;
            if (_currentRun?.Id == id)
                return new(id, startIndex, limit, run.DecisionCount, _currentRun.Decisions.Values.Skip(startIndex).Take(limit).ToArray());
            if (_finishedDecisions?.Id == id)
                return new(id, startIndex, limit, run.DecisionCount, _finishedDecisions.Decisions.Values.Skip(startIndex).Take(limit).ToArray());
            var result = new List<SyncDecision>(limit);
            if (run.DecisionCount == 0) return new(id, startIndex, limit, 0, result);
            try
            {
                using var input = File.OpenRead(DecisionPath(id));
                if (input.Length > MaximumDecisionFileBytes) throw new InvalidDataException();
                using var reader = new StreamReader(input, Encoding.UTF8);
                var buffer = new char[4096];
                for (var index = 0; index < Math.Min(run.DecisionCount, startIndex + limit); index++)
                {
                    var count = 0;
                    int next;
                    while ((next = reader.Read()) is not (-1 or 10))
                    {
                        if (count == buffer.Length) throw new InvalidDataException();
                        buffer[count++] = (char)next;
                    }
                    if (count == 0) throw new InvalidDataException();
                    if (index < startIndex) continue;
                    var decision = JsonSerializer.Deserialize<SyncDecision>(buffer.AsSpan(0, count), new JsonSerializerOptions { MaxDepth = 4 })
                        ?? throw new InvalidDataException();
                    decision = SanitizeDecision(decision);
                    if (decision.Action == "Pending" && run.State != "Running")
                        decision = decision with { Action = "Error", Reasons = decision.Reasons.Append(run.State == "Interrupted" ? "Interrupted" : "NotPublished").Distinct(StringComparer.Ordinal).ToArray() };
                    result.Add(decision);
                }
                return new(id, startIndex, limit, run.DecisionCount, result);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or InvalidDataException)
            {
                _storageCode = "HistoryDetailsUnavailable";
                return new(id, startIndex, limit, run.DecisionCount, [], "HistoryDetailsUnavailable");
            }
        }
    }

    public void RecordDecision(string contentKey, string name, string action, params string[] reasons)
    {
        var safe = SanitizeDecision(new(contentKey, name, action, reasons));
        lock (_gate)
        {
            if (_disposed || _currentRun is not { } run) return;
            if (run.Decisions.TryGetValue(safe.ContentKey, out var previous))
            {
                // A publication outcome does not erase a genuine preservation/error/pause observation.
                var keepPrevious = safe.Action == "Pending"
                    || (previous.Action is "Preserved" or "Error" or "ProviderPaused") && (safe.Action is "Updated" or "Unchanged");
                safe = safe with
                {
                    Action = keepPrevious ? previous.Action : safe.Action,
                    Reasons = previous.Reasons.Concat(safe.Reasons).Distinct(StringComparer.Ordinal).Take(DecisionReasons.Count).ToArray()
                };
            }
            else if (run.Decisions.Count >= MaximumDecisions) return;
            run.Decisions[safe.ContentKey] = safe;
            run.DecisionsDirty = true;
            ScheduleWrite();
        }
    }

    internal IDisposable BeginTitle(string contentKey, string name)
    {
        var previous = _title.Value;
        _title.Value = (contentKey, name);
        return new TitleScope(() => _title.Value = previous);
    }

    private sealed class TitleScope(Action release) : IDisposable
    {
        public void Dispose() => release();
    }

    private void AddHistory(SyncRunSnapshot run)
    {
        _history.RemoveAll(saved => saved.Id == run.Id);
        _history.Insert(0, run);
        if (_history.Count > 20) _history.RemoveRange(20, _history.Count - 20);
    }

    private string DecisionDirectory => Path.Combine(Path.GetDirectoryName(_path)!, "sync-history");
    private string DecisionPath(string id) => Path.Combine(DecisionDirectory, id + ".jsonl");

    private void WriteDecisions(RunState run)
    {
        if (!run.DecisionsDirty) return;
        Directory.CreateDirectory(DecisionDirectory);
        var path = DecisionPath(run.Id);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.WriteThrough };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            {
                foreach (var decision in run.Decisions.Values)
                {
                    JsonSerializer.Serialize(stream, decision);
                    stream.WriteByte(10);
                }
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            run.DecisionsDirty = false;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private void PruneDecisionFiles()
    {
        if (!Directory.Exists(DecisionDirectory)) return;
        var retained = _history.Select(run => run.Id).ToHashSet(StringComparer.Ordinal);
        if (_currentRun is { } current) retained.Add(current.Id);
        foreach (var path in Directory.EnumerateFiles(DecisionDirectory, "*.jsonl"))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            if (Guid.TryParseExact(id, "N", out _) && !retained.Contains(id)) File.Delete(path);
        }
    }

    internal static string SafeName(string? value)
        => string.IsNullOrWhiteSpace(value) || value.Length > 200 || value.Any(char.IsControl) || SecretTextPattern.IsMatch(value)
            ? "Title (name withheld)" : value;

    private static string SafeKey(string? value)
        => value is not null && value.Length <= 100 && ContentKeyPattern.IsMatch(value) ? value
            : "redacted:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty)));

    private static string? SafeTarget(string? value)
        => value is null ? null : value.Length == 64 && value.All(char.IsAsciiHexDigit) ? value : SafeKey(value);

    private static string SafeScope(string? value) => value is "All" or "Catalog" or "Series" or "FollowedSeries" ? value : "All";

    private static SyncDecision SanitizeDecision(SyncDecision decision) => new(SafeKey(decision.ContentKey), SafeName(decision.Name),
        SafeCode(decision.Action, Actions, "Error"), (decision.Reasons ?? []).Where(DecisionReasons.Contains).Distinct(StringComparer.Ordinal).Take(DecisionReasons.Count).ToArray());
}

public sealed record SyncDecision(string ContentKey, string Name, string Action, IReadOnlyList<string> Reasons);
public sealed record SyncDecisionPage(string Id, int StartIndex, int Limit, int TotalCount, IReadOnlyList<SyncDecision> Items, string? StorageCode = null);
