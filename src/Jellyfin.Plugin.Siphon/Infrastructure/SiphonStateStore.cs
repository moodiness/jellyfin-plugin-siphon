using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Siphon.Identity;

namespace Jellyfin.Plugin.Siphon.Infrastructure;

public sealed class SiphonStateStore : ISiphonStateStore, IDisposable
{
    internal const int MaximumItems = 100_000;
    private readonly string _path;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private Snapshot _snapshot;

    /// <summary>Process-local commit revision for consumers caching immutable snapshots.</summary>
    public long Revision => Volatile.Read(ref _snapshot).Read.Revision!.Value;

    public SiphonStateStore(SiphonPaths paths)
    {
        _path = Path.Combine(paths.DataDirectory, "state.json");
        if (!File.Exists(_path))
        {
            _snapshot = CreateSnapshot([], 0);
            return;
        }

        // Existing catalogs can exceed 256 MiB within the supported item count.
        // Stream persisted metadata; never discard ownership data because of its total byte size.
        using var stream = File.OpenRead(_path);
        try
        {
            // Read only the version first; the current schema needs no intermediate JSON tree.
            // Property order is unrestricted, including legacy documents with Items before Version.
            var version = ReadVersion(stream);
            stream.Position = 0;
            StateDocument? document;
            if (version == 1)
            {
                var root = JsonNode.Parse(stream) as JsonObject
                    ?? throw new InvalidDataException("Siphon state is empty or corrupt; the original file was retained.");
                if (root["Items"] is not JsonArray legacyItems)
                    throw new InvalidDataException("Siphon legacy state has no item array; the original file was retained.");
                // One-time data migration: keep paths and item keys so native watch state stays attached.
                foreach (var node in legacyItems)
                {
                    var item = node as JsonObject ?? throw new InvalidDataException("Invalid legacy item.");
                    var imdb = item["ImdbId"]?.GetValue<string>() ?? throw new InvalidDataException("Missing legacy identity.");
                    var type = item["Type"]?.GetValue<string>() ?? throw new InvalidDataException("Missing legacy type.");
                    item["ContentId"] = imdb;
                    item["ContentKey"] = type + ":" + imdb;
                    item["ProviderIds"] = new JsonObject { ["Imdb"] = imdb };
                    item["StreamIdentities"] = new JsonArray(new JsonObject { ["Type"] = type, ["VideoId"] = item["VideoId"]?.GetValue<string>() });
                    item.Remove("ImdbId");
                }
                root["Version"] = 2;
                document = root.Deserialize<StateDocument>();
            }
            else if (version == 2)
            {
                document = JsonSerializer.Deserialize<StateDocument>(stream);
            }
            else
            {
                throw new InvalidDataException("Siphon state has an unsupported format; the original file was retained.");
            }
            if (document?.Version != 2 || document.Items is null)
                throw new InvalidDataException("Siphon state has an unsupported format; the original file was retained.");
            _snapshot = CreateSnapshot(document.Items, 0);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Siphon state is corrupt; restore a valid backup before retrying. The original file was retained.", exception);
        }
    }

    public IReadOnlyList<ManagedItem> GetItems() => Array.AsReadOnly(Volatile.Read(ref _snapshot).Items.Select(Clone).ToArray());

    public SiphonReadSnapshot GetReadSnapshot() => Volatile.Read(ref _snapshot).Read;
    public ManagedItemSnapshot? ReadByKey(string key) => GetReadSnapshot().FindByKey(key);
    public ManagedItemSnapshot? ReadByPath(string path) => GetReadSnapshot().FindByPath(path);
    public ManagedItemSnapshot? ReadByContentKey(string contentKey) => GetReadSnapshot().FindByContentKey(contentKey);

    public ManagedItem? FindByKey(string key) => ReadByKey(key)?.ToMutable();

    public ManagedItem? FindByPath(string path) => ReadByPath(path)?.ToMutable();

    public ManagedItem? FindByContentKey(string contentKey) => ReadByContentKey(contentKey)?.ToMutable();

    public async Task SaveAsync(IReadOnlyList<ManagedItem> items, CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = CreateSnapshot(items, checked(Revision + 1));
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            await using (var stream = new FileStream(temporary, options))
            {
                await JsonSerializer.SerializeAsync(stream, new StateDocument { Version = 2, Items = next.Items }, cancellationToken: cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, _path, overwrite: true);
            // Cancellation after the commit point must not leave memory behind durable state.
            Volatile.Write(ref _snapshot, next);
        }
        finally
        {
            try
            {
                if (temporary is not null)
                {
                    File.Delete(temporary);
                }
            }
            finally
            {
                _writer.Release();
            }
        }
    }

    private static Snapshot CreateSnapshot(IReadOnlyList<ManagedItem> source, long revision)
    {
        if (source.Count > MaximumItems)
            throw new InvalidOperationException("Siphon is limited to 100,000 managed items.");
        var items = new ManagedItem[source.Count];
        for (var index = 0; index < source.Count; index++)
        {
            var item = source[index];
            if (item is null || string.IsNullOrWhiteSpace(item.Key) || string.IsNullOrWhiteSpace(item.Path)
                || !Path.IsPathFullyQualified(item.Path) || string.IsNullOrWhiteSpace(item.ContentId) || string.IsNullOrWhiteSpace(item.ContentKey)
                || string.IsNullOrWhiteSpace(item.VideoId) || string.IsNullOrWhiteSpace(item.Name)
                || item.Type is not ("movie" or "series") || item.Owners is null || item.ProviderIds is null || item.EpisodeProviderIds is null
                || item.MissingOwners is null || item.MissingOwners.Any(owner => !item.Owners.Contains(owner, StringComparer.Ordinal))
                || item.Genres is null || item.StreamIdentities is null || item.StreamIdentities.Any(identity => identity is null || string.IsNullOrWhiteSpace(identity.Type) || string.IsNullOrWhiteSpace(identity.VideoId))
                || item.ProductionLocations is null || item.People is null || item.EpisodeGenres is null
                || item.EpisodeProductionLocations is null || item.EpisodePeople is null
                || item.People.Concat(item.EpisodePeople).Any(person => person is null || string.IsNullOrWhiteSpace(person.Name) || person.Type is not ("Actor" or "Director" or "Writer" or "Producer"))
                || item.Owners.Any(string.IsNullOrWhiteSpace))
            {
                throw new InvalidDataException("Siphon state contains an invalid managed item.");
            }

            items[index] = Clone(item);
        }

        return new Snapshot(items, new SiphonReadSnapshot(items, revision));
    }

    internal static ManagedItem Clone(ManagedItem item) => item with
    {
        MetadataProvenance = Metadata.MetadataProvenance.Clone(item.MetadataProvenance),
        Owners = (string[])item.Owners.Clone(),
        MissingOwners = (string[])item.MissingOwners.Clone(),
        Genres = (string[])item.Genres.Clone(),
        ProductionLocations = (string[])item.ProductionLocations.Clone(),
        People = (ManagedPerson[])item.People.Clone(),
        EpisodeGenres = (string[])item.EpisodeGenres.Clone(),
        EpisodeProductionLocations = (string[])item.EpisodeProductionLocations.Clone(),
        EpisodePeople = (ManagedPerson[])item.EpisodePeople.Clone(),
        StreamIdentities = (StreamIdentity[])item.StreamIdentities.Clone(),
        ProviderIds = new Dictionary<string, string>(item.ProviderIds, StringComparer.OrdinalIgnoreCase),
        EpisodeProviderIds = new Dictionary<string, string>(item.EpisodeProviderIds, StringComparer.OrdinalIgnoreCase)
    };

    public void Dispose() => _writer.Dispose();

    private sealed record Snapshot(ManagedItem[] Items, SiphonReadSnapshot Read);

    private static int ReadVersion(Stream stream)
    {
        // Inspect tokens incrementally instead of buffering an ignored Items property.
        // Writer output puts Version first; reordered documents retain the same semantics.
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        var buffered = 0;
        var state = new JsonReaderState();
        var firstBlock = true;
        var versionValue = false;
        try
        {
            while (true)
            {
                var read = stream.Read(buffer.AsSpan(buffered));
                buffered += read;
                var final = read == 0;
                if (firstBlock && buffered >= 3 && buffer[0] == 0xef && buffer[1] == 0xbb && buffer[2] == 0xbf)
                {
                    buffer.AsSpan(3, buffered - 3).CopyTo(buffer);
                    buffered -= 3;
                }
                firstBlock = false;
                var reader = new Utf8JsonReader(buffer.AsSpan(0, buffered), final, state);
                while (reader.Read())
                {
                    if (versionValue)
                        return reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var version)
                            ? version : throw new JsonException("Invalid Siphon state version.");
                    versionValue = reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1
                        && reader.ValueTextEquals("Version"u8);
                }
                if (final) return 0;
                var consumed = checked((int)reader.BytesConsumed);
                state = reader.CurrentState;
                buffered -= consumed;
                buffer.AsSpan(consumed, buffered).CopyTo(buffer);
                if (buffered < buffer.Length) continue;
                var capacity = (int)Math.Min((long)buffer.Length * 2, Array.MaxLength);
                if (capacity <= buffer.Length) throw new JsonException("Siphon state contains an oversized JSON token.");
                var larger = ArrayPool<byte>.Shared.Rent(capacity);
                buffer.AsSpan(0, buffered).CopyTo(larger);
                ArrayPool<byte>.Shared.Return(buffer);
                buffer = larger;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private sealed class StateDocument
    {
        public int Version { get; init; }
        public ManagedItem[]? Items { get; init; }
    }
}
