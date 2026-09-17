using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Siphon.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Siphon.Protocol;

public sealed record RegisteredAddon(ConfiguredAddon Configuration, StremioManifest Manifest);

public sealed class AddonRegistry(StremioClient client, ConfigurationAccessor configuration, ILogger<AddonRegistry> logger)
{
    private readonly ConcurrentDictionary<string, Entry> _cache = new(StringComparer.Ordinal);
    private sealed record Entry(DateTimeOffset Expires, Lazy<Task<StremioManifest>> Value);
    public void Invalidate() => _cache.Clear();
    internal static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public ConfiguredAddon[] GetConfiguredForUser(Guid userId)
        => ConfiguredForUser(configuration.Current, userId).Where(addon => addon.Enabled).Take(256).ToArray();

    internal static string PlaybackKey(PluginConfiguration config, Guid userId)
        => Digest(config.EnableP2p + "\n" + string.Join("\n", ConfiguredForUser(config, userId)
            .Where(addon => addon.Enabled).Take(256).Select(addon => addon.Id + "\n" + addon.ManifestUrl + "\n" + addon.DisplayName)));

    private static ConfiguredAddon[] ConfiguredForUser(PluginConfiguration config, Guid userId)
    {
        var profile = userId == Guid.Empty ? null : config.UserProfiles.FirstOrDefault(profile => profile.UserId == userId);
        return profile is { OverrideAddons: true } ? profile.Addons : config.Addons;
    }

    public Task<IReadOnlyList<RegisteredAddon>> GetEnabledAsync(CancellationToken ct)
        => GetEnabledForUserAsync(Guid.Empty, ct);

    public async Task<IReadOnlyList<RegisteredAddon>> GetEnabledForUserAsync(Guid userId, CancellationToken ct)
    {
        var addons = GetConfiguredForUser(userId);
        var output = new RegisteredAddon?[addons.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, addons.Length), new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Clamp(configuration.Current.MaxConcurrentRequests, 1, 16) }, async (i, token) =>
        {
            var addon = addons[i];
            var key = userId.ToString("N") + ":" + addon.Id + ":" + Digest(addon.ManifestUrl);
            try
            {
                if (_cache.Count > 512) _cache.Clear();
                var now = DateTimeOffset.UtcNow;
                var entry = _cache.AddOrUpdate(key, _ => MakeEntry(addon.ManifestUrl, now), (_, old) => old.Expires > now ? old : MakeEntry(addon.ManifestUrl, now));
                var manifest = await entry.Value.Value.WaitAsync(token).ConfigureAwait(false);
                output[i] = new(addon, manifest);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                _cache.TryRemove(key, out _);
                logger.LogWarning("Siphon manifest unavailable for installation {InstallationId}", addon.Id);
            }
        }).ConfigureAwait(false);
        return output.OfType<RegisteredAddon>().ToArray();
    }
    private Entry MakeEntry(string url, DateTimeOffset now) => new(now.AddMinutes(2), new(() => client.GetManifestAsync(url, CancellationToken.None), LazyThreadSafetyMode.ExecutionAndPublication));
    public static bool Supports(StremioManifest manifest, string resource, string type, string id)
    {
        if (resource == "catalog") return manifest.Catalogs.Any(c => c.Type == type && c.Id == id);
        return manifest.Resources.Any(r => r.Name == resource
            && (r.IsObject ? r.Types is not null && r.Types.Contains(type, StringComparer.Ordinal) : manifest.Types.Contains(type, StringComparer.Ordinal))
            && ((r.IsObject ? r.IdPrefixes : manifest.IdPrefixes) is not { } prefixes || prefixes.Any(p => id.StartsWith(p, StringComparison.Ordinal))));
    }
}
