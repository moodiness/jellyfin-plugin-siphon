using System.Text.Json;
using Jellyfin.Plugin.Siphon.Protocol;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Siphon.Infrastructure;

/// <summary>Credential-free observational state, separate from library ownership. Writes are coalesced and atomic.</summary>
public sealed partial class SyncDiagnostics : IDisposable
{
    private const int MaximumRecords = 256;
    private const int MaximumFileBytes = 4 * 1024 * 1024;
    private static readonly HashSet<string> ResourceNames = new(StringComparer.Ordinal) { "catalog", "meta", "stream", "subtitles", "addon_catalog" };
    private static readonly HashSet<string> TypeNames = new(StringComparer.Ordinal) { "movie", "series", "anime", "tv", "channel", "other" };
    private static readonly HashSet<string> SyncCodes = new(StringComparer.Ordinal)
    {
        "ManifestUnavailable", "CatalogUnavailable", "InvalidCatalog", "IncompleteCatalog", "MetadataUnavailable", "RequestFailed", "SyncFailed", "Cancelled"
    };
    private static readonly HashSet<string> ManifestCodes = new(StringComparer.Ordinal) { "Ok", "ManifestUnavailable", "Timeout", "Cancelled" };
    private static readonly HashSet<string> RunKinds = new(StringComparer.Ordinal) { "Catalogs", "FullRefresh", "FollowedSeries", "Targeted" };
    private static readonly HashSet<string> RunStages = new(StringComparer.Ordinal) { "Preparing", "Catalogs", "Metadata", "Saving", "Publishing", "Credits", "Finalizing" };
    private static readonly HashSet<string> RunUnits = new(StringComparer.Ordinal) { "Catalogs", "Titles", "Series", "Items", "Percent" };
    private static readonly HashSet<string> Providers = new(StringComparer.Ordinal) { "Tmdb", "Tvdb", "Fanart", "MdbList" };
    private static readonly HashSet<string> ProviderCodes = new(StringComparer.Ordinal)
    {
        "Timeout", "NetworkError", "ProviderUnavailable", "AuthenticationFailed", "MissingCredentials", "MissingIdentifier",
        "NotFound", "InvalidResponse", "RateLimited", "ResponseTooLarge", "CircuitOpen", "ProviderPaused", "ProviderError"
    };
    private readonly object _gate = new();
    private readonly string _path;
    private readonly ILogger<SyncDiagnostics> _logger;
    private readonly Dictionary<string, InstallationDiagnostic> _records = new(StringComparer.Ordinal);
    private readonly Timer _writer;
    private bool _pendingWrite;
    private bool _disposed;
    private string? _storageCode;
    private RunState? _currentRun;
    private SyncRunSnapshot? _lastRun;

    public SyncDiagnostics(SiphonPaths paths, ILogger<SyncDiagnostics> logger)
    {
        _path = Path.Combine(paths.DataDirectory, "diagnostics.json");
        _logger = logger;
        Load();
        _writer = new Timer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (_history.Count > 0)
        {
            _pendingWrite = true;
            Flush();
        }
    }

    public DiagnosticSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return new(_storageCode, _records.Values.Select(Sanitize).ToArray(), _currentRun?.Snapshot(), _lastRun);
        }
    }

    public SyncRunStatus GetRunStatus()
    {
        lock (_gate) return new(_currentRun?.Snapshot(), _lastRun);
    }

    public void BeginRun(string kind, string scope = "All", string? targetKey = null)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _currentRun = new RunState(SafeCode(kind, RunKinds, "Catalogs"), SafeScope(scope), SafeTarget(targetKey));
            ScheduleWrite();
            Flush();
        }
    }

    public void ReportStage(string stage, string unit, int completed, int total)
    {
        lock (_gate)
        {
            if (_disposed || _currentRun is not { } run) return;
            run.Stage = SafeCode(stage, RunStages, "Preparing");
            run.Unit = SafeCode(unit, RunUnits, "Items");
            run.Total = Math.Max(0, total);
            run.Completed = Math.Clamp(completed, 0, run.Total);
            ScheduleWrite();
        }
    }

    public void SetPrioritySeries(int count)
    {
        lock (_gate)
        {
            if (_disposed || _currentRun is not { } run) return;
            run.PrioritySeries = Math.Max(0, count);
            ScheduleWrite();
        }
    }

    public void SetSummary(int added, int updated, int unchanged, int removed, int preserved, int failedSubscriptions)
    {
        lock (_gate)
        {
            if (_disposed || _currentRun is not { } run) return;
            run.Added = Math.Max(0, added);
            run.Updated = Math.Max(0, updated);
            run.Unchanged = Math.Max(0, unchanged);
            run.Removed = Math.Max(0, removed);
            run.Preserved = Math.Max(0, preserved);
            run.FailedSubscriptions = Math.Max(0, failedSubscriptions);
            ScheduleWrite();
        }
    }

    public void RecordProviderRequest(string provider, bool cached)
    {
        lock (_gate)
        {
            if (_disposed || _currentRun is not { } run || !Providers.Contains(provider)) return;
            var counters = run.Provider(provider);
            if (cached) counters.CacheHits++;
            else counters.Requests++;
            if (cached && _title.Value is { } title)
                RecordDecision(title.Key, title.Name, "Pending", "CacheHit");
            ScheduleWrite();
        }
    }

    public void RecordProviderError(string provider, string code)
    {
        lock (_gate)
        {
            if (_disposed || _currentRun is not { } run || !Providers.Contains(provider)) return;
            var counters = run.Provider(provider);
            var safe = SafeCode(code, ProviderCodes, "ProviderError");
            counters.Errors[safe] = counters.Errors.GetValueOrDefault(safe) + 1;
            if (_title.Value is { } title)
                RecordDecision(title.Key, title.Name, safe is "ProviderPaused" or "CircuitOpen" or "RateLimited" ? "ProviderPaused" : "Pending",
                    safe is "ProviderPaused" or "CircuitOpen" or "RateLimited" ? "ProviderPaused" : "ProviderError");
            ScheduleWrite();
        }
    }

    public void FinishRun(string state)
    {
        lock (_gate)
        {
            if (_disposed || _currentRun is not { } run) return;
            foreach (var key in run.Decisions.Keys.ToArray())
            {
                var decision = run.Decisions[key];
                if (decision.Action == "Pending")
                    run.Decisions[key] = decision with { Action = "Error", Reasons = decision.Reasons.Append(state == "Cancelled" ? "Cancelled" : "NotPublished").Distinct(StringComparer.Ordinal).ToArray() };
            }
            run.DecisionsDirty = true;
            _lastRun = run.Snapshot() with
            {
                State = state is "Completed" or "Cancelled" ? state : "Failed",
                FinishedAtUtc = DateTimeOffset.UtcNow
            };
            AddHistory(_lastRun);
            _finishedDecisions = run;
            _currentRun = null;
            ScheduleWrite();
            Flush();
        }
    }

    public void RecordSync(string addonId, bool success, string? errorCode = null)
    {
        if (!Guid.TryParse(addonId, out var installationId)) return;
        lock (_gate)
        {
            if (_disposed) return;
            var id = installationId.ToString("N");
            var current = GetOrCreate(id);
            var now = DateTimeOffset.UtcNow;
            _records[id] = success
                ? current with { LastSuccessUtc = now }
                : current with { LastFailureUtc = now, LastErrorCode = SafeCode(errorCode, SyncCodes, "SyncFailed") };
            ScheduleWrite();
        }
    }

    public ManifestCheckDiagnostic RecordManifestCheck(string addonId, bool success, string code, long elapsedMilliseconds, StremioManifest? manifest)
    {
        var check = new ManifestCheckDiagnostic(DateTimeOffset.UtcNow, Math.Clamp(elapsedMilliseconds, 0, 120000), success,
            success ? "Ok" : SafeCode(code, ManifestCodes, "ManifestUnavailable"),
            success && manifest is not null ? Capabilities(manifest) : null);
        if (!Guid.TryParse(addonId, out var installationId)) return check;
        lock (_gate)
        {
            if (_disposed) return check;
            var id = installationId.ToString("N");
            _records[id] = GetOrCreate(id) with { LastManifestCheck = check };
            ScheduleWrite();
        }

        return check;
    }

    private InstallationDiagnostic GetOrCreate(string id)
    {
        if (_records.TryGetValue(id, out var record)) return record;
        if (_records.Count >= MaximumRecords)
        {
            var oldest = _records.Values.MinBy(LastObservation);
            if (oldest is not null) _records.Remove(oldest.InstallationId);
        }

        return new(id, null, null, null, null);
    }

    private static DateTimeOffset LastObservation(InstallationDiagnostic value)
    {
        var latest = value.LastSuccessUtc ?? DateTimeOffset.MinValue;
        if (value.LastFailureUtc is { } failure && failure > latest) latest = failure;
        if (value.LastManifestCheck?.CheckedAtUtc is { } check && check > latest) latest = check;
        return latest;
    }

    private void ScheduleWrite()
    {
        if (_pendingWrite) return;
        _pendingWrite = true;
        _writer.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            using var stream = File.OpenRead(_path);
            if (stream.Length > MaximumFileBytes) throw new InvalidDataException();
            var document = JsonSerializer.Deserialize<DiagnosticDocument>(stream, new JsonSerializerOptions { MaxDepth = 16 });
            if (document is null || document.Version is not (1 or 2 or 3) || document.Addons is null || document.Addons.Length > MaximumRecords) throw new InvalidDataException();
            foreach (var record in document.Addons)
            {
                if (record is null || !Guid.TryParse(record.InstallationId, out var id)) throw new InvalidDataException();
                var safe = Sanitize(record) with { InstallationId = id.ToString("N") };
                _records[safe.InstallationId] = safe;
            }
            foreach (var saved in (document.History ?? []).Take(20).Reverse())
                if (SanitizeRun(saved) is { } historical) AddHistory(historical);
            _lastRun = SanitizeRun(document.LastRun);
            if (_lastRun is not null) AddHistory(_lastRun);
            // A process exit cannot leave a live task. Preserve both the prior last run and the interruption.
            if (SanitizeRun(document.CurrentRun) is { } interrupted)
            {
                _lastRun = interrupted with { State = "Interrupted", FinishedAtUtc = null };
                AddHistory(_lastRun);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException)
        {
            _records.Clear();
            _lastRun = null;
            _history.Clear();
            _storageCode = exception is JsonException or InvalidDataException or NotSupportedException ? "DiagnosticsCorrupt" : "DiagnosticsUnavailable";
            _logger.LogWarning("Siphon diagnostic history could not be loaded; library ownership is unaffected.");
        }
    }

    private void Flush()
    {
        lock (_gate)
        {
            if (!_pendingWrite) return;
            _pendingWrite = false;
            string? temporary = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                if (_finishedDecisions is { } finished) WriteDecisions(finished);
                if (_currentRun is { } running) WriteDecisions(running);
                temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.WriteThrough };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var stream = new FileStream(temporary, options))
                {
                    JsonSerializer.Serialize(stream, new DiagnosticDocument(3, _records.Values.ToArray(), _currentRun?.Snapshot(), _lastRun, _history.ToArray()));
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporary, _path, overwrite: true);
                _finishedDecisions = null;
                PruneDecisionFiles();
                _storageCode = null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                _storageCode = "DiagnosticsUnavailable";
                _logger.LogWarning("Siphon diagnostic history could not be saved; library ownership is unaffected.");
            }
            finally
            {
                if (temporary is not null)
                {
                    try { File.Delete(temporary); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                }
            }
        }
    }

    private static string SafeCode(string? code, HashSet<string> allowed, string fallback) => code is not null && allowed.Contains(code) ? code : fallback;
    private static string[] SafeTypes(IEnumerable<string>? types) => types?.Where(TypeNames.Contains).Distinct(StringComparer.Ordinal).Take(TypeNames.Count).ToArray() ?? [];

    private static ManifestCapabilities Capabilities(StremioManifest manifest) => new(SafeTypes(manifest.Types),
        manifest.Resources.Where(resource => ResourceNames.Contains(resource.Name)).Select(resource =>
            new DiagnosticResource(resource.Name, resource.IsObject, SafeTypes(resource.IsObject ? resource.Types : manifest.Types),
                (resource.IsObject ? resource.IdPrefixes : manifest.IdPrefixes) is not null)).Take(32).ToArray());

    private static InstallationDiagnostic Sanitize(InstallationDiagnostic record) => record with
    {
        LastErrorCode = record.LastErrorCode is null ? null : SafeCode(record.LastErrorCode, SyncCodes, "SyncFailed"),
        LastManifestCheck = record.LastManifestCheck is not { } check ? null : check with
        {
            Code = check.Success ? "Ok" : SafeCode(check.Code, ManifestCodes, "ManifestUnavailable"),
            ElapsedMilliseconds = Math.Clamp(check.ElapsedMilliseconds, 0, 120000),
            Capabilities = !check.Success || check.Capabilities is not { } capabilities ? null : new ManifestCapabilities(SafeTypes(capabilities.Types),
                (capabilities.Resources ?? []).Where(resource => resource is not null && ResourceNames.Contains(resource.Name)).Take(32)
                    .Select(resource => resource with { Types = SafeTypes(resource.Types) }).ToArray())
        }
    };

    private static SyncRunSnapshot? SanitizeRun(SyncRunSnapshot? run)
    {
        if (run is null) return null;
        var total = Math.Max(0, run.Total);
        return run with
        {
            Kind = SafeCode(run.Kind, RunKinds, "Catalogs"),
            State = run.State is "Running" or "Completed" or "Cancelled" or "Interrupted" ? run.State : "Failed",
            Stage = SafeCode(run.Stage, RunStages, "Preparing"),
            Id = Guid.TryParse(run.Id, out var id) ? id.ToString("N") : Guid.NewGuid().ToString("N"),
            Scope = SafeScope(run.Scope),
            TargetKey = SafeTarget(run.TargetKey),
            DecisionCount = Math.Clamp(run.DecisionCount, 0, MaximumDecisions),
            Unit = SafeCode(run.Unit, RunUnits, "Items"),
            Completed = Math.Clamp(run.Completed, 0, total),
            Total = total,
            PrioritySeries = Math.Max(0, run.PrioritySeries),
            Added = Math.Max(0, run.Added),
            Updated = Math.Max(0, run.Updated),
            Unchanged = Math.Max(0, run.Unchanged),
            Removed = Math.Max(0, run.Removed),
            Preserved = Math.Max(0, run.Preserved),
            FailedSubscriptions = Math.Max(0, run.FailedSubscriptions),
            Providers = (run.Providers ?? []).Where(provider => provider is not null && Providers.Contains(provider.Provider))
                .DistinctBy(provider => provider.Provider).Take(Providers.Count)
                .Select(provider => new SyncProviderSnapshot(provider.Provider, Math.Max(0, provider.Requests), Math.Max(0, provider.CacheHits),
                    (provider.Errors ?? new Dictionary<string, int>()).Where(error => ProviderCodes.Contains(error.Key))
                        .ToDictionary(error => error.Key, error => Math.Max(0, error.Value), StringComparer.Ordinal))).ToArray()
        };
    }

    private sealed class RunState(string kind, string scope, string? targetKey)
    {
        private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;
        private readonly Dictionary<string, ProviderCounters> _providers = new(StringComparer.Ordinal);
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public Dictionary<string, SyncDecision> Decisions { get; } = new(StringComparer.Ordinal);
        public bool DecisionsDirty { get; set; }
        public string Stage { get; set; } = "Preparing";
        public string Unit { get; set; } = "Items";
        public int Completed { get; set; }
        public int Total { get; set; }
        public int PrioritySeries { get; set; }
        public int Added { get; set; }
        public int Updated { get; set; }
        public int Unchanged { get; set; }
        public int Removed { get; set; }
        public int Preserved { get; set; }
        public int FailedSubscriptions { get; set; }

        public ProviderCounters Provider(string name)
        {
            if (!_providers.TryGetValue(name, out var counters)) _providers[name] = counters = new();
            return counters;
        }

        public SyncRunSnapshot Snapshot() => new(kind, "Running", _startedAtUtc, null, Stage, Unit, Completed, Total,
            PrioritySeries, Added, Updated, Unchanged, Removed, Preserved, FailedSubscriptions,
            _providers.Select(provider => new SyncProviderSnapshot(provider.Key, provider.Value.Requests, provider.Value.CacheHits,
                new Dictionary<string, int>(provider.Value.Errors, StringComparer.Ordinal))).ToArray(), Id, scope, targetKey, Decisions.Count);
    }

    private sealed class ProviderCounters
    {
        public int Requests { get; set; }
        public int CacheHits { get; set; }
        public Dictionary<string, int> Errors { get; } = new(StringComparer.Ordinal);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _writer.Dispose();
            Flush();
        }
    }

    private sealed record DiagnosticDocument(int Version, InstallationDiagnostic[] Addons, SyncRunSnapshot? CurrentRun = null,
        SyncRunSnapshot? LastRun = null, SyncRunSnapshot[]? History = null);
}

public sealed record DiagnosticSnapshot(string? StorageCode, IReadOnlyList<InstallationDiagnostic> Addons,
    SyncRunSnapshot? CurrentRun = null, SyncRunSnapshot? LastRun = null);
public sealed record SyncRunStatus(SyncRunSnapshot? CurrentRun, SyncRunSnapshot? LastRun);
public sealed record SyncRunSnapshot(string Kind, string State, DateTimeOffset StartedAtUtc, DateTimeOffset? FinishedAtUtc,
    string Stage, string Unit, int Completed, int Total, int PrioritySeries, int Added, int Updated, int Unchanged,
    int Removed, int Preserved, int FailedSubscriptions, IReadOnlyList<SyncProviderSnapshot> Providers,
    string Id = "", string Scope = "All", string? TargetKey = null, int DecisionCount = 0);
public sealed record SyncProviderSnapshot(string Provider, int Requests, int CacheHits, IReadOnlyDictionary<string, int> Errors);
public sealed record InstallationDiagnostic(string InstallationId, DateTimeOffset? LastSuccessUtc, DateTimeOffset? LastFailureUtc,
    string? LastErrorCode, ManifestCheckDiagnostic? LastManifestCheck);
public sealed record ManifestCheckDiagnostic(DateTimeOffset CheckedAtUtc, long ElapsedMilliseconds, bool Success, string Code, ManifestCapabilities? Capabilities);
public sealed record ManifestCapabilities(IReadOnlyList<string> Types, IReadOnlyList<DiagnosticResource> Resources);
/// <summary>Prefixes are deliberately not exported: arbitrary addon identifiers can embed credentials.</summary>
public sealed record DiagnosticResource(string Name, bool IsObject, IReadOnlyList<string> Types, bool IdPrefixesRestricted);
