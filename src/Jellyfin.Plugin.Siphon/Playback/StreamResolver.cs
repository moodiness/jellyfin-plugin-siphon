using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Identity;
using Jellyfin.Plugin.Siphon.Protocol;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Siphon.Playback;

public sealed record AddonSourceDiagnostic(string InstallationId, string Name, long ElapsedMilliseconds, string Outcome, int Accepted, IReadOnlyDictionary<string, int> Rejected);
public sealed record SourceResolution(IReadOnlyList<ResolvedStream> Sources, IReadOnlyList<AddonSourceDiagnostic> Addons, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

public sealed class StreamResolver(StremioClient client, AddonRegistry registry, ConfigurationAccessor configuration, ILogger<StreamResolver> logger, SourceBindingStore bindings)
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private sealed record CacheEntry(DateTimeOffset Expires, Lazy<Task<SourceResolution>> Value);
    private static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase) { "Host", "Connection", "Content-Length", "Transfer-Encoding", "TE", "Trailer", "Upgrade", "Keep-Alive", "Proxy-Authorization", "Proxy-Authenticate", "Range", "If-Range", "Accept-Encoding", "Forwarded", "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto" };
    public void Invalidate() => _cache.Clear();
    public void Invalidate(Func<Guid, bool> changedUser)
    {
        foreach (var key in _cache.Keys)
            if (key.Length >= 33 && Guid.TryParseExact(key.AsSpan(0, 32), "N", out var userId) && changedUser(userId))
                _cache.TryRemove(key, out _);
    }
    public void Invalidate(string itemKey, Guid userId)
    {
        var prefix = userId.ToString("N") + ":" + itemKey + ":";
        foreach (var key in _cache.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal))) _cache.TryRemove(key, out _);
    }
    private string ConfigurationKey(Guid userId) => AddonRegistry.PlaybackKey(configuration.Current, userId);
    public async Task<IReadOnlyList<ResolvedStream>> GetSourcesAsync(ManagedItem item, Guid userId, CancellationToken ct)
        => (await GetResolutionAsync(item, userId, ct).ConfigureAwait(false)).Sources;
    public async Task<SourceResolution> GetResolutionAsync(ManagedItem item, Guid userId, CancellationToken ct)
    {
        var configurationKey = ConfigurationKey(userId);
        var identities = item.StreamIdentities.Length > 0 ? item.StreamIdentities : [new StreamIdentity(item.Type, item.VideoId)];
        var fingerprint = new StringBuilder(configurationKey).Append('\n').Append(item.VideoId.Length).Append(':').Append(item.VideoId);
        foreach (var identity in identities)
            fingerprint.Append('\n').Append(identity.Type.Length).Append(':').Append(identity.Type).Append(identity.VideoId.Length).Append(':').Append(identity.VideoId);
        var key = userId.ToString("N") + ":" + item.Key + ":" + AddonRegistry.Digest(fingerprint.ToString());
        var now = DateTimeOffset.UtcNow;
        if (_cache.Count > 512)
            foreach (var expired in _cache.OrderBy(pair => pair.Value.Expires).Take(Math.Max(1, _cache.Count - 512)).ToArray()) _cache.TryRemove(expired);
        CacheEntry Create() => new(now.AddSeconds(45), new(() => Resolve(item, identities, userId, now), LazyThreadSafetyMode.ExecutionAndPublication));
        var entry = _cache.AddOrUpdate(key, _ => Create(), (_, old) => old.Expires > now ? old : Create());
        try
        {
            var result = await entry.Value.Value.WaitAsync(ct).ConfigureAwait(false);
            return ConfigurationKey(userId) == configurationKey ? result : new([], [], now, now);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { _cache.TryRemove(new KeyValuePair<string, CacheEntry>(key, entry)); throw; }
    }
    private async Task<SourceResolution> Resolve(ManagedItem item, IReadOnlyList<StreamIdentity> identities, Guid userId, DateTimeOffset created)
    {
        var configured = registry.GetConfiguredForUser(userId);
        var output = new IReadOnlyList<ResolvedStream>?[configured.Length];
        var diagnostics = new AddonSourceDiagnostic[configured.Length];
        var manifestStart = Stopwatch.GetTimestamp();
        var enabled = await registry.GetEnabledForUserAsync(userId, CancellationToken.None).ConfigureAwait(false);
        var manifestElapsed = (long)Stopwatch.GetElapsedTime(manifestStart).TotalMilliseconds;
        await Parallel.ForEachAsync(Enumerable.Range(0, configured.Length), new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(configuration.Current.MaxConcurrentRequests, 1, 16) }, async (i, ct) =>
        {
            var installation = configured[i];
            var addon = enabled.FirstOrDefault(a => a.Configuration.Id == installation.Id && a.Configuration.ManifestUrl == installation.ManifestUrl);
            var rejected = new Dictionary<string, int>(StringComparer.Ordinal);
            void Reject(string reason) => rejected[reason] = rejected.GetValueOrDefault(reason) + 1;
            var start = Stopwatch.GetTimestamp();
            var outcome = "Unavailable";
            var candidates = new List<(string Identity, ResolvedStream Stream)>();
            try
            {
                if (addon is null) return;
                var resourceIdentity = identities.FirstOrDefault(identity => AddonRegistry.Supports(addon.Manifest, "stream", identity.Type, identity.VideoId));
                if (resourceIdentity is null) { outcome = "Unsupported"; return; }
                var streams = await client.GetStreamsAsync(installation.ManifestUrl, resourceIdentity.Type, resourceIdentity.VideoId, ct).ConfigureAwait(false);
                foreach (var stream in streams.Take(256))
                {
                    P2p.P2pSource? p2p;
                    try { p2p = P2p.P2pSource.Parse(stream.InfoHash, stream.FileIndex, stream.FileName, stream.Sources, stream.Url?.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) == true ? stream.Url : null); }
                    catch (ArgumentException) { Reject("InvalidP2p"); continue; }
                    if (p2p is not null && !configuration.Current.EnableP2p) { Reject("P2pDisabled"); continue; }
                    if (p2p is not null) p2p = p2p with { Season = item.Season, Episode = item.Episode };
                    Uri? url = null;
                    if (p2p is null && (!Uri.TryCreate(stream.Url, UriKind.Absolute, out url) || url.Scheme is not ("http" or "https") || url.UserInfo.Length != 0 || url.Fragment.Length != 0)) { Reject("UnsupportedUrl"); continue; }
                    if (stream.UnsupportedHeaders || !ValidHeaders(stream.RequestHeaders)) { Reject("UnsupportedHeaders"); continue; }
                    var headers = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(stream.RequestHeaders, StringComparer.OrdinalIgnoreCase));
                    // User and installation fingerprint are part of every durable source identity.
                    var identity = new StringBuilder(userId.ToString("N")).Append('\n').Append(installation.Id).Append('\n').Append(AddonRegistry.Digest(installation.ManifestUrl)).Append('\n').Append(stream.Name).Append('\n').Append(stream.FileName ?? (p2p is null ? url!.AbsoluteUri : p2p.InfoHash + ":" + p2p.FileIndex));
                    identity.Append('\n').Append(stream.Size);
                    var sourceId = AddonRegistry.Digest(identity.ToString());
                    var name = SafeDisplay(string.Join(" · ", new[] { string.IsNullOrWhiteSpace(installation.DisplayName) ? addon.Manifest.Name : installation.DisplayName, stream.Name, stream.Description }.Where(s => !string.IsNullOrWhiteSpace(s))));
                    candidates.Add((identity.ToString(), new(sourceId, name, url ?? new Uri("urn:siphon:p2p:" + sourceId), headers, stream.FileName, stream.Size) { UserId = userId, P2p = p2p }));
                }
                output[i] = bindings.Bind(userId.ToString("N") + ":" + item.Key, candidates);
                outcome = candidates.Count == 0 ? "Empty" : "Available";
            }
            catch (Exception) { logger.LogWarning("Siphon streams unavailable for installation {InstallationId}", installation.Id); }
            finally
            {
                diagnostics[i] = new(installation.Id, SafeDisplay(installation.DisplayName), manifestElapsed + (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds, outcome, output[i]?.Count ?? 0, rejected);
            }
        }).ConfigureAwait(false);
        return new(Array.AsReadOnly(output.Where(x => x is not null).SelectMany(x => x!).ToArray()), diagnostics, created, created.AddSeconds(45));
    }
    internal static string SafeDisplay(string value)
    {
        var words = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var safe = string.Join(" ", words.Where(w => !w.Contains("://", StringComparison.Ordinal) && !w.Contains("magnet:", StringComparison.OrdinalIgnoreCase) && !w.Contains("Authorization", StringComparison.OrdinalIgnoreCase) && !w.Contains("token=", StringComparison.OrdinalIgnoreCase)));
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
