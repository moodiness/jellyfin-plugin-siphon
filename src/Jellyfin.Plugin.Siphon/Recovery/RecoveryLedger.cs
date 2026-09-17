using System.Text.Json;
using Jellyfin.Database.Implementations;
using Jellyfin.Plugin.Siphon.Identity;
using Jellyfin.Plugin.Siphon.Infrastructure;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.Siphon.Recovery;

/// <summary>Records proven ownership before native deletion destroys provider identity.</summary>
public sealed class RecoveryLedger(SiphonPaths paths, IDbContextFactory<JellyfinDbContext> database)
{
    private const int MaximumEntries = 20000;
    private readonly string _path = Path.Combine(paths.DataDirectory, "recovery-ledger.json");
    private readonly SemaphoreSlim _writer = new(1, 1);

    // Caller holds SynchronizationGate and MutationGate. Capture the full deletion set, including descendants.
    public async Task CaptureAsync(IReadOnlyList<BaseItem> nativeItems, IReadOnlyList<ManagedItem> previousItems, CancellationToken ct)
    {
        var byKey = previousItems.ToDictionary(item => item.Key, StringComparer.Ordinal);
        var byContent = previousItems.GroupBy(item => item.ContentKey).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var known = new Dictionary<Guid, (BaseItem Native, ManagedItem Managed, string Key)>();
        foreach (var native in nativeItems)
        {
            var key = native.GetProviderId("Siphon");
            if (key is null || native is not (Movie or Episode or Series or Season)) continue;
            var managed = byKey.GetValueOrDefault(key) ?? byContent.GetValueOrDefault(key);
            if (managed is null && native is Season season)
            {
                var content = key.LastIndexOf(":season:", StringComparison.Ordinal);
                if (content > 0 && byContent.TryGetValue(key[..content], out var first))
                    managed = previousItems.FirstOrDefault(item => item.ContentKey == first.ContentKey && item.Season == season.IndexNumber);
            }
            if (managed is not null) known[native.Id] = (native, managed, key);
        }
        if (known.Count == 0) return;
        await _writer.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var records = Load().ToList();
            await using var context = await database.CreateDbContextAsync(ct).ConfigureAwait(false);
            foreach (var batch in known.Keys.Chunk(256))
            {
                var rows = await context.UserData.AsNoTracking().Where(row => batch.Contains(row.ItemId)).ToArrayAsync(ct).ConfigureAwait(false);
                foreach (var row in rows.Where(RecoveryPolicy.Meaningful))
                {
                    if (string.IsNullOrEmpty(row.CustomDataKey)) continue;
                    var (native, item, key) = known[row.ItemId];
                    // Persist identity only: no upstream image URLs, provider credentials or arbitrary metadata.
                    var identity = new ManagedItem
                    {
                        Key = item.Key,
                        ContentKey = item.ContentKey,
                        ContentId = item.ContentId,
                        Type = item.Type,
                        VideoId = item.VideoId,
                        Name = item.Name,
                        SeriesName = item.SeriesName,
                        Season = item.Season,
                        Episode = item.Episode,
                        Year = item.Year,
                        Path = item.Path,
                        ProviderIds = item.ProviderIds,
                        EpisodeProviderIds = item.EpisodeProviderIds,
                        SearchResourceType = item.SearchResourceType,
                        StreamIdentities = item.StreamIdentities
                    };
                    records.RemoveAll(record => record.UserId == row.UserId && record.DataKey == row.CustomDataKey && record.NativeId == native.Id);
                    records.Add(new(native.Id, key, native.GetType().Name, identity, row.UserId, row.CustomDataKey,
                        RecoveryPolicy.Fingerprint(row, false), DateTime.UtcNow));
                }
            }
            var retained = records.OrderByDescending(record => record.CapturedAtUtc).Take(MaximumEntries).ToArray();
            Directory.CreateDirectory(paths.DataDirectory);
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.Asynchronous | FileOptions.WriteThrough
                };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using (var stream = new FileStream(temporary, options))
                {
                    await JsonSerializer.SerializeAsync(stream, new Document(1, retained), cancellationToken: ct).ConfigureAwait(false);
                    await stream.FlushAsync(ct).ConfigureAwait(false);
                    stream.Flush(true);
                }
                File.Move(temporary, _path, true);
            }
            finally { File.Delete(temporary); }
        }
        finally { _writer.Release(); }
    }

    internal Entry[] Load()
    {
        if (!File.Exists(_path)) return [];
        if (new FileInfo(_path).Length > 64 * 1024 * 1024) throw new InvalidDataException("Recovery ownership ledger exceeds its safety limit.");
        using var stream = File.OpenRead(_path);
        var document = JsonSerializer.Deserialize<Document>(stream);
        if (document is not { Version: 1 } || document.Items is null || document.Items.Length > MaximumEntries)
            throw new InvalidDataException("Recovery ownership ledger is invalid.");
        return document.Items;
    }

    internal sealed record Entry(Guid NativeId, string NativeKey, string NativeType, ManagedItem Identity,
        Guid UserId, string DataKey, string Fingerprint, DateTime CapturedAtUtc);
    private sealed record Document(int Version, Entry[] Items);
}
