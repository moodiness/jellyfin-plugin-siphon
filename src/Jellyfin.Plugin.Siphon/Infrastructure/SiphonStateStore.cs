using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Siphon.Identity;

namespace Jellyfin.Plugin.Siphon.Infrastructure;

public sealed class SiphonStateStore : ISiphonStateStore, IDisposable
{
    private readonly string _path;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private Snapshot _snapshot;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public SiphonStateStore(SiphonPaths paths)
    {
        _path = Path.Combine(paths.DataDirectory, "state.json");
        if (!File.Exists(_path))
        {
            _snapshot = CreateSnapshot([]);
            return;
        }

        // Corruption is fatal, not an invitation to discard ownership information.
        using var stream = File.OpenRead(_path);
        var root = JsonNode.Parse(stream) as JsonObject
            ?? throw new InvalidDataException("Siphon state is empty or corrupt.");
        var version = root["Version"]?.GetValue<int>();
        if (version == 1 && root["Items"] is JsonArray legacyItems)
        {
            // One-time data migration: keep paths and item keys so watch state stays attached.
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
        }

        var document = root.Deserialize<StateDocument>() ?? throw new InvalidDataException("Invalid Siphon state.");
        if (document.Version != 2 || document.Items is null)
        {
            throw new InvalidDataException("Siphon state has an unsupported format.");
        }

        _snapshot = CreateSnapshot(document.Items);
    }

    public IReadOnlyList<ManagedItem> GetItems() => Array.AsReadOnly(Volatile.Read(ref _snapshot).Items.Select(Clone).ToArray());

    public ManagedItem? FindByKey(string key) => Volatile.Read(ref _snapshot).Keys.TryGetValue(key, out var item) ? Clone(item) : null;

    public ManagedItem? FindByPath(string path) => Volatile.Read(ref _snapshot).Paths.TryGetValue(Path.GetFullPath(path), out var item) ? Clone(item) : null;

    public ManagedItem? FindByContentKey(string contentKey) => Volatile.Read(ref _snapshot).Contents.TryGetValue(contentKey, out var item) ? Clone(item) : null;

    public async Task SaveAsync(IReadOnlyList<ManagedItem> items, CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = CreateSnapshot(items);
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

    private static Snapshot CreateSnapshot(IReadOnlyList<ManagedItem> source)
    {
        var items = new ManagedItem[source.Count];
        var keys = new Dictionary<string, ManagedItem>(source.Count, StringComparer.Ordinal);
        var paths = new Dictionary<string, ManagedItem>(source.Count, PathComparer);
        var contents = new Dictionary<string, ManagedItem>(StringComparer.Ordinal);
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

            var owned = Clone(item);
            if (!keys.TryAdd(owned.Key, owned) || !paths.TryAdd(Path.GetFullPath(owned.Path), owned))
            {
                throw new InvalidDataException("Siphon state contains duplicate identities or paths.");
            }

            items[index] = owned;
            contents.TryAdd(owned.ContentKey, owned);
        }

        return new Snapshot(items, keys, paths, contents);
    }

    private static ManagedItem Clone(ManagedItem item) => item with
    {
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

    private sealed record Snapshot(ManagedItem[] Items, Dictionary<string, ManagedItem> Keys, Dictionary<string, ManagedItem> Paths, Dictionary<string, ManagedItem> Contents);

    private sealed class StateDocument
    {
        public int Version { get; init; }
        public ManagedItem[]? Items { get; init; }
    }
}
