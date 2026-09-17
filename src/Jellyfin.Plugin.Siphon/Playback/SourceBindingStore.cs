using System.Globalization;
using System.Text.Json;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Protocol;

namespace Jellyfin.Plugin.Siphon.Playback;

/// <summary>Retains mirror ordinals without persisting upstream URLs or credentials.</summary>
public sealed class SourceBindingStore
{
    private const int MaximumBindings = 65536;
    private const int MaximumBytes = 32 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly string _path;
    private readonly int _maximumBindings;
    private Dictionary<string, Dictionary<string, List<int>>>? _groups;
    private int _bindingCount;

    public SourceBindingStore(SiphonPaths paths)
        : this(Path.Combine(paths.DataDirectory, "source-bindings.json"), MaximumBindings)
    {
    }

    internal SourceBindingStore(string path, int maximumBindings = MaximumBindings)
    {
        _path = path;
        _maximumBindings = maximumBindings;
    }

    internal IReadOnlyList<ResolvedStream> Bind(string itemKey, IReadOnlyList<(string Identity, ResolvedStream Stream)> candidates)
    {
        lock (_gate)
        {
            Load();
            var changed = new Dictionary<string, Dictionary<string, List<int>>>(StringComparer.Ordinal);
            var occurrences = new Dictionary<(string Group, string Endpoint), int>();
            var result = new List<ResolvedStream>(candidates.Count);
            var added = 0;
            foreach (var (identity, source) in candidates)
            {
                var groupKey = AddonRegistry.Digest(itemKey + "\n" + source.Id);
                var endpoint = EndpointDigest(source);
                if (!changed.TryGetValue(groupKey, out var group))
                    _groups!.TryGetValue(groupKey, out group);
                var occurrenceKey = (groupKey, endpoint);
                var occurrence = occurrences.GetValueOrDefault(occurrenceKey);
                occurrences[occurrenceKey] = occurrence + 1;
                int ordinal;
                if (group is not null && group.TryGetValue(endpoint, out var ordinals) && occurrence < ordinals.Count)
                {
                    ordinal = ordinals[occurrence];
                }
                else
                {
                    if (_bindingCount + added >= _maximumBindings)
                        throw new InvalidOperationException("Source identity storage capacity reached.");
                    if (!changed.ContainsKey(groupKey))
                    {
                        group = group is null ? new(StringComparer.Ordinal)
                            : group.ToDictionary(pair => pair.Key, pair => new List<int>(pair.Value), StringComparer.Ordinal);
                        changed.Add(groupKey, group);
                    }
                    // Retired bindings stay reserved; disappearance never shifts another mirror into its ID.
                    ordinal = group!.Values.Sum(values => values.Count);
                    if (!group.TryGetValue(endpoint, out var assigned)) group.Add(endpoint, assigned = []);
                    assigned.Add(ordinal);
                    added++;
                }
                var id = ordinal == 0 ? source.Id : AddonRegistry.Digest(identity + "\n" + ordinal.ToString(CultureInfo.InvariantCulture));
                result.Add(source with { Id = id });
            }
            if (changed.Count > 0)
            {
                var updated = new Dictionary<string, Dictionary<string, List<int>>>(_groups!, StringComparer.Ordinal);
                foreach (var pair in changed) updated[pair.Key] = pair.Value;
                // Publish only after durable assignment. A failed write cannot create a transient identity
                // that would be assigned to a different endpoint after restart.
                Save(updated);
                _groups = updated;
                _bindingCount += added;
            }
            return result;
        }
    }

    private void Load()
    {
        if (_groups is not null) return;
        if (!File.Exists(_path))
        {
            _groups = new(StringComparer.Ordinal);
            return;
        }
        using var file = File.OpenRead(_path);
        if (file.Length > MaximumBytes) throw new InvalidDataException("Source identity storage exceeds its limit.");
        var document = JsonSerializer.Deserialize<Document>(file);
        if (document is not { Version: 1, Groups: not null }) throw new InvalidDataException("Invalid source identity storage.");
        var count = 0;
        foreach (var (key, group) in document.Groups)
        {
            if (!IsDigest(key) || group is null || group.Count == 0) throw new InvalidDataException("Invalid source identity storage.");
            var used = new HashSet<int>();
            foreach (var (endpoint, ordinals) in group)
            {
                if (!IsDigest(endpoint) || ordinals is null || ordinals.Count == 0) throw new InvalidDataException("Invalid source identity storage.");
                foreach (var ordinal in ordinals)
                    if (ordinal < 0 || !used.Add(ordinal) || ++count > _maximumBindings)
                        throw new InvalidDataException("Invalid source identity storage.");
            }
            if (used.Max() != used.Count - 1) throw new InvalidDataException("Invalid source identity storage.");
        }
        _groups = document.Groups;
        _bindingCount = count;
    }

    private void Save(Dictionary<string, Dictionary<string, List<int>>> groups)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temporary, options))
            {
                JsonSerializer.Serialize(file, new Document { Version = 1, Groups = groups });
                if (file.Length > MaximumBytes) throw new InvalidDataException("Source identity storage exceeds its limit.");
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string EndpointDigest(ResolvedStream source)
    {
        if (source.P2p is { } torrent)
            return AddonRegistry.Digest(torrent.InfoHash + "\n" + torrent.FileIndex + "\n" + torrent.FileName + "\n" + string.Join('\n', torrent.Trackers));
        // A filename is stable content identity; only discard recognized signing parameters when
        // the endpoint also has a media path or retains a content-identifying query. In particular,
        // /get?file=A&token=... and /get?file=B&token=... must never become the same endpoint.
        var uri = source.Url;
        if (string.IsNullOrEmpty(source.FileName) || uri.Query.Length == 0) return AddonRegistry.Digest(uri.AbsoluteUri);
        var query = uri.Query[1..].Split('&').Where(part => !IsAuthenticationParameter(part)).ToArray();
        var extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        var mediaPath = extension is ".mkv" or ".mp4" or ".webm" or ".m3u8" or ".m3u" or ".ts" or ".m4v" or ".avi" or ".mov";
        if (!mediaPath && query.Length == 0) return AddonRegistry.Digest(uri.AbsoluteUri);
        return AddonRegistry.Digest(uri.GetLeftPart(UriPartial.Path) + (query.Length == 0 ? string.Empty : "?" + string.Join('&', query)));
    }

    private static bool IsAuthenticationParameter(string part)
    {
        var equals = part.IndexOf('=');
        var key = Uri.UnescapeDataString(equals < 0 ? part : part[..equals]);
        return key.ToLowerInvariant() is "token" or "access_token" or "signature" or "expires"
            or "x-amz-algorithm" or "x-amz-credential" or "x-amz-date" or "x-amz-expires"
            or "x-amz-signedheaders" or "x-amz-signature" or "x-amz-security-token"
            or "policy" or "key-pair-id";
    }

    private static bool IsDigest(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);

    private sealed class Document
    {
        public Document() { }
        public required int Version { get; set; }
        public required Dictionary<string, Dictionary<string, List<int>>> Groups { get; set; }
    }
}
