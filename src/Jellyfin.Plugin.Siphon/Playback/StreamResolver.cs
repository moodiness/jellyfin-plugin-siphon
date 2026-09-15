using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Identity;
using Jellyfin.Plugin.Siphon.Protocol;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Siphon.Playback;

public sealed class StreamResolver(StremioClient client, AddonRegistry registry, ConfigurationAccessor configuration, ILogger<StreamResolver> logger)
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private sealed record CacheEntry(DateTimeOffset Expires, Lazy<Task<IReadOnlyList<ResolvedStream>>> Value);
    private static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase) { "Host", "Connection", "Content-Length", "Transfer-Encoding", "TE", "Trailer", "Upgrade", "Keep-Alive", "Proxy-Authorization", "Proxy-Authenticate", "Range", "Accept-Encoding", "Forwarded", "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto" };
    public void Invalidate() => _cache.Clear();
    public async Task<IReadOnlyList<ResolvedStream>> GetSourcesAsync(ManagedItem item, CancellationToken ct)
    {
        var configurationKey = string.Join("\n", configuration.Current.Addons.Where(a => a.Enabled).Select(a => a.Id + "\n" + a.ManifestUrl + "\n" + a.DisplayName));
        var identities = item.StreamIdentities.Length > 0 ? item.StreamIdentities : [new StreamIdentity(item.Type, item.VideoId)];
        var fingerprint = new StringBuilder(configurationKey).Append('\n').Append(item.VideoId.Length).Append(':').Append(item.VideoId);
        foreach (var identity in identities)
            fingerprint.Append('\n').Append(identity.Type.Length).Append(':').Append(identity.Type).Append(identity.VideoId.Length).Append(':').Append(identity.VideoId);
        var key = item.Key + ":" + AddonRegistry.Digest(fingerprint.ToString());
        if (_cache.Count > 512) _cache.Clear();
        var now = DateTimeOffset.UtcNow;
        CacheEntry Create() => new(now.AddSeconds(45), new(() => Resolve(identities), LazyThreadSafetyMode.ExecutionAndPublication));
        var entry = _cache.AddOrUpdate(key, _ => Create(), (_, old) => old.Expires > now ? old : Create());
        try
        {
            var result = await entry.Value.Value.WaitAsync(ct).ConfigureAwait(false);
            var currentKey = string.Join("\n", configuration.Current.Addons.Where(a => a.Enabled).Select(a => a.Id + "\n" + a.ManifestUrl + "\n" + a.DisplayName));
            return currentKey == configurationKey ? result : [];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { _cache.TryRemove(new KeyValuePair<string, CacheEntry>(key, entry)); throw; }
    }
    private async Task<IReadOnlyList<ResolvedStream>> Resolve(IReadOnlyList<StreamIdentity> identities)
    {
        var addons = (await registry.GetEnabledAsync(CancellationToken.None).ConfigureAwait(false))
            .Select(addon => (Addon: addon, Identity: identities.FirstOrDefault(identity => AddonRegistry.Supports(addon.Manifest, "stream", identity.Type, identity.VideoId))))
            .Where(request => request.Identity is not null).ToArray();
        var output = new List<ResolvedStream>?[addons.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, addons.Length), new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(configuration.Current.MaxConcurrentRequests, 1, 16) }, async (i, ct) =>
        {
            var (addon, resourceIdentity) = addons[i];
            try
            {
                var streams = await client.GetStreamsAsync(addon.Configuration.ManifestUrl, resourceIdentity!.Type, resourceIdentity.VideoId, ct).ConfigureAwait(false);
                var results = new List<ResolvedStream>();
                foreach (var stream in streams.Take(256))
                {
                    if (!Uri.TryCreate(stream.Url, UriKind.Absolute, out var url) || (url.Scheme != "http" && url.Scheme != "https") || url.UserInfo.Length != 0 || url.Fragment.Length != 0) continue;
                    if (stream.UnsupportedHeaders || !ValidHeaders(stream.RequestHeaders)) { logger.LogWarning("Siphon skipped unsupported proxy headers from installation {InstallationId}", addon.Configuration.Id); continue; }
                    var headers = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(stream.RequestHeaders, StringComparer.OrdinalIgnoreCase));
                    var identity = new StringBuilder(addon.Configuration.Id).Append('\n').Append(stream.Name).Append('\n').Append(stream.FileName ?? url.AbsoluteUri);
                    identity.Append('\n').Append(stream.Size);
                    var name = string.Join(" · ", new[] { string.IsNullOrWhiteSpace(addon.Configuration.DisplayName) ? addon.Manifest.Name : addon.Configuration.DisplayName, stream.Name, stream.Description }.Where(s => !string.IsNullOrWhiteSpace(s)));
                    // Addon-controlled display text must not echo credential-bearing URLs.
                    name = SafeDisplay(name);
                    results.Add(new(AddonRegistry.Digest(identity.ToString()), name, url, headers, stream.FileName, stream.Size));
                }
                output[i] = results;
            }
            catch (Exception) { logger.LogWarning("Siphon streams unavailable for installation {InstallationId}", addon.Configuration.Id); }
        }).ConfigureAwait(false);
        return Array.AsReadOnly(output.Where(x => x is not null).SelectMany(x => x!).DistinctBy(x => x.Id).ToArray());
    }
    private static string SafeDisplay(string value)
    {
        var words = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var safe = string.Join(" ", words.Where(w => !w.Contains("://", StringComparison.Ordinal) && !w.Contains("Authorization", StringComparison.OrdinalIgnoreCase)));
        return safe.Length > 300 ? safe[..300] : safe;
    }
    private static bool ValidHeaders(IReadOnlyDictionary<string, string> headers)
    {
        if (headers.Count > 32) return false;
        var total = 0;
        foreach (var h in headers)
        {
            total += h.Key.Length + h.Value.Length;
            if (total > 16384 || h.Key.Length == 0 || h.Key.Length > 128 || ForbiddenHeaders.Contains(h.Key) || h.Key.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase)
                || h.Key.Any(c => !(char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c)))
                || h.Value.Any(c => c < 32 || c == 127 || c > 255)) return false;
        }
        return true;
    }
}
