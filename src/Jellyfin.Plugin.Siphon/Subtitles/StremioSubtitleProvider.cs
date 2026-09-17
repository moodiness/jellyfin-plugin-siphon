using System.Globalization;
using System.Text.Json;
using Jellyfin.Plugin.Siphon.Configuration;
using Jellyfin.Plugin.Siphon.Identity;
using Jellyfin.Plugin.Siphon.Infrastructure;
using Jellyfin.Plugin.Siphon.Playback;
using Jellyfin.Plugin.Siphon.Protocol;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.Siphon.Subtitles;

/// <summary>Provides subtitles advertised by enabled Stremio addons for Siphon-managed items.</summary>
public sealed class StremioSubtitleProvider : ISubtitleProvider, IDisposable
{
    private const int MaximumJsonBytes = 2 * 1024 * 1024;
    private const int MaximumSubtitleBytes = 8 * 1024 * 1024;
    private const int MaximumSubtitleEntries = 256;
    private readonly SiphonItemLocator _locator;
    private readonly AddonRegistry _registry;
    private readonly StreamResolver _resolver;
    private readonly ISafeHttpClient _http;
    private readonly ConfigurationAccessor _configuration;
    private readonly Lazy<Dictionary<string, string>> _languages;
    private readonly SubtitleTicketStore _tickets;
    private readonly SemaphoreSlim _requests = new(16, 16);
    private readonly PlaybackAccess? _access;

    /// <summary>Initializes the Jellyfin subtitle provider.</summary>
    public StremioSubtitleProvider(
        SiphonItemLocator locator,
        AddonRegistry registry,
        StreamResolver resolver,
        ISafeHttpClient http,
        ConfigurationAccessor configuration,
        ILocalizationManager localization,
        PlaybackAccess access)
        : this(locator, registry, resolver, http, configuration, localization, TimeProvider.System, 4096, TimeSpan.FromMinutes(5))
    {
        _access = access;
    }

    internal StremioSubtitleProvider(
        SiphonItemLocator locator,
        AddonRegistry registry,
        StreamResolver resolver,
        ISafeHttpClient http,
        ConfigurationAccessor configuration,
        ILocalizationManager localization,
        TimeProvider clock,
        int ticketCapacity,
        TimeSpan ticketLifetime)
    {
        _locator = locator;
        _registry = registry;
        _resolver = resolver;
        _http = http;
        _configuration = configuration;
        _languages = new(() => LanguageMap(localization.GetCultures()));
        _tickets = new SubtitleTicketStore(clock, Math.Clamp(ticketCapacity, 1, 16384), ticketLifetime);
    }
    internal StremioSubtitleProvider(
        ISiphonStateStore state,
        AddonRegistry registry,
        StreamResolver resolver,
        ISafeHttpClient http,
        ConfigurationAccessor configuration,
        ILocalizationManager localization,
        TimeProvider clock,
        int ticketCapacity,
        TimeSpan ticketLifetime)
        : this(new SiphonItemLocator(state), registry, resolver, http, configuration, localization, clock, ticketCapacity, ticketLifetime)
    {
    }

    public string Name => "Siphon";

    public IEnumerable<VideoContentType> SupportedMediaTypes => [VideoContentType.Movie, VideoContentType.Episode];

    public async Task<IEnumerable<RemoteSubtitleInfo>> Search(SubtitleSearchRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Language) || string.IsNullOrWhiteSpace(request.MediaPath))
            return [];

        // Automated refreshes must not cause subtitle fetches. Jellyfin's explicit remote search sets this false.
        if (request.IsAutomated || request.IsPerfectMatch)
            return [];

        var item = _locator.Find(request.MediaPath, out var sourceId);
        if (item is null || item.Type is not ("movie" or "series"))
            return [];
        var userId = _access?.CurrentUser()?.Id ?? Guid.Empty;
        if (_access is not null && (userId == Guid.Empty || _access.FindVideo(item.Key, userId, sourceId) is null)) return [];

        var requestedLanguage = CanonicalLanguage(request.Language);
        if (requestedLanguage is null)
            return [];

        // These are the exact identities persisted by the identity sync. Never infer a Jellyfin ID or numbering.
        var identities = item.StreamIdentities
            .Where(identity => identity is not null
                && identity.Type.Length is > 0 and <= 64
                && identity.VideoId.Length is > 0 and <= 2048)
            .Distinct()
            .Take(32)
            .ToArray();
        if (identities.Length == 0)
            return [];

        var addons = await _registry.GetEnabledForUserAsync(userId, cancellationToken).ConfigureAwait(false);
        var requests = addons.Take(64)
            .Select(addon => (Addon: addon, Identities: identities.Where(identity => AddonRegistry.Supports(addon.Manifest, "subtitles", identity.Type, identity.VideoId)).ToArray()))
            .Where(requested => requested.Identities.Length > 0)
            .ToArray();
        if (requests.Length == 0)
            return [];

        var extras = await ResolveExtrasAsync(item, sourceId, userId, cancellationToken).ConfigureAwait(false);
        var output = new List<RemoteSubtitleInfo>[requests.Length];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, requests.Length),
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Clamp(_configuration.Current.MaxConcurrentRequests, 1, 16)
            },
            async (index, token) =>
            {
                var (addon, addonIdentities) = requests[index];
                var found = new List<RemoteSubtitleInfo>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var display = SafeDisplay(string.IsNullOrWhiteSpace(addon.Configuration.DisplayName) ? addon.Manifest.Name : addon.Configuration.DisplayName);
                var manifestDigest = AddonRegistry.Digest(addon.Configuration.ManifestUrl);
                try
                {
                    foreach (var identity in addonIdentities)
                    {
                        var uri = StremioClient.ResourceUri(addon.Configuration.ManifestUrl, "subtitles", identity.Type, identity.VideoId, extras);
                        var subtitles = await GetSubtitlesResponseAsync(uri, token).ConfigureAwait(false);
                        if (!IsInstallationCurrent(addon.Configuration.Id, manifestDigest, userId)) break;
                        foreach (var subtitle in subtitles)
                        {
                            if (found.Count >= 64 || !TrySubtitleUri(subtitle.Url, out var subtitleUri))
                                continue;

                            var language = CanonicalLanguage(subtitle.Language);
                            if (language != requestedLanguage || !seen.Add(subtitle.Id))
                                continue;

                            var id = _tickets.Add(new SubtitleCandidate(
                                addon.Configuration.Id,
                                manifestDigest,
                                item.Path,
                                item.Key,
                                subtitle.Id,
                                subtitleUri,
                                language)
                            { UserId = userId });
                            found.Add(new RemoteSubtitleInfo
                            {
                                Id = id,
                                ProviderName = Name,
                                Name = display + " · " + language + " · " + (found.Count + 1).ToString(CultureInfo.InvariantCulture),
                                ThreeLetterISOLanguageName = language,
                                Format = SubtitleFormat(subtitleUri, null)
                            });
                        }
                        if (found.Count >= 64) break;
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    // An unavailable or malformed addon contributes no subtitles; never disclose its URI.
                }

                output[index] = found;
            }).ConfigureAwait(false);

        return output.Where(list => list is not null).SelectMany(list => list!).Where(result =>
            _tickets.Get(result.Id) is { } candidate && IsCandidateCurrent(candidate)).ToArray();
    }

    public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
    {
        var candidate = _tickets.Get(id);
        if (candidate is null || !IsCandidateCurrent(candidate))
            throw new InvalidOperationException("Subtitle is unavailable. Search again.");

        var content = await FetchAsync(candidate.Url, MaximumSubtitleBytes, cancellationToken).ConfigureAwait(false);
        try
        {
            var format = SubtitleFormat(candidate.Url, content.MediaType);
            if (format is null) throw new InvalidDataException();
            var bytes = SubtitleText.Normalize(content.Bytes, format, content.Charset);
            if (_tickets.Get(id) is null || !IsCandidateCurrent(candidate)) throw new InvalidDataException();
            return new SubtitleResponse
            {
                Language = candidate.Language,
                Format = format,
                Stream = new MemoryStream(bytes, writable: false)
            };
        }
        catch (Exception)
        {
            throw new InvalidOperationException("Subtitle is unavailable or is not a supported text subtitle.");
        }
    }

    private async Task<IReadOnlyDictionary<string, string>?> ResolveExtrasAsync(ManagedItem item, string? sourceId, Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            var streams = await _resolver.GetSourcesAsync(item, userId, cancellationToken).ConfigureAwait(false);
            var source = sourceId is null
                ? streams.FirstOrDefault()
                : streams.FirstOrDefault(stream => stream.Id == sourceId);
            if (source is null)
                return null;

            var extras = new Dictionary<string, string>(StringComparer.Ordinal);
            if (source.FileName is { Length: > 0 and <= 256 } fileName && fileName.IndexOfAny(['\r', '\n', '\0']) < 0)
                extras["filename"] = Path.GetFileName(fileName);
            if (source.Size is >= 0)
                extras["videoSize"] = source.Size.Value.ToString(CultureInfo.InvariantCulture);
            return extras.Count == 0 ? null : extras;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<SubtitleWire>> GetSubtitlesResponseAsync(Uri uri, CancellationToken cancellationToken)
    {
        var content = await FetchAsync(uri, MaximumJsonBytes, cancellationToken).ConfigureAwait(false);

        try
        {
            using var document = JsonDocument.Parse(content.Bytes, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("subtitles", out var entries)
                || entries.ValueKind != JsonValueKind.Array
                || entries.GetArrayLength() > MaximumSubtitleEntries)
                return [];

            var output = new List<SubtitleWire>();
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !Text(entry, "id", out var id) || id.Length is < 1 or > 512
                    || !Text(entry, "url", out var url) || url.Length is < 1 or > 8192
                    || !Text(entry, "lang", out var lang) || lang.Length is < 1 or > 64)
                    continue;
                output.Add(new(id, url, lang));
            }

            return output;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task<(byte[] Bytes, string? MediaType, string? Charset)> FetchAsync(Uri uri, int maximum, CancellationToken cancellationToken)
    {
        using var timeout = CreateTimeout(cancellationToken);
        var acquired = false;
        try
        {
            await _requests.WaitAsync(timeout.Token).ConfigureAwait(false);
            acquired = true;
            using var response = await _http.SendAsync(uri, HttpMethod.Get, null, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new InvalidDataException();
            var bytes = await ReadBoundedAsync(response, maximum, timeout.Token).ConfigureAwait(false);
            if (bytes is null) throw new InvalidDataException();
            var contentType = response.Content.Headers.ContentType;
            return (bytes, contentType?.MediaType, contentType?.CharSet);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException("Subtitle request timed out.");
        }
        catch (Exception)
        {
            // Exception messages from HTTP handlers can contain complete credential-bearing URLs.
            throw new InvalidOperationException("Subtitle request failed or exceeded the size limit.");
        }
        finally
        {
            if (acquired) _requests.Release();
        }
    }

    private bool IsCandidateCurrent(SubtitleCandidate candidate)
    {
        if (!IsInstallationCurrent(candidate.InstallationId, candidate.ManifestDigest, candidate.UserId)
            || (_access is not null && (_access.CurrentUser()?.Id != candidate.UserId
                || _access.FindVideo(candidate.ItemKey, candidate.UserId) is null)))
            return false;

        var item = _locator.Find(candidate.ItemPath, out _);
        return item is not null && string.Equals(item.Key, candidate.ItemKey, StringComparison.Ordinal);
    }

    private bool IsInstallationCurrent(string installationId, string manifestDigest, Guid userId) =>
        _registry.GetConfiguredForUser(userId).Any(addon => addon.Enabled && addon.Id == installationId
            && AddonRegistry.Digest(addon.ManifestUrl) == manifestDigest);

    private string? CanonicalLanguage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64) return null;
        return _languages.Value.GetValueOrDefault(value.Trim());
    }
    private static Dictionary<string, string> LanguageMap(IEnumerable<CultureDto> cultures)
    {
        // Cache only the server's finite ISO table, never arbitrary addon labels.
        var languages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in cultures)
        {
            if (culture.ThreeLetterISOLanguageName is not { Length: 3 } code || !code.All(char.IsAsciiLetter)) continue;
            code = code.ToLowerInvariant();
            foreach (var alias in culture.ThreeLetterISOLanguageNames) languages.TryAdd(alias, code);
            if (!string.IsNullOrEmpty(culture.TwoLetterISOLanguageName)) languages.TryAdd(culture.TwoLetterISOLanguageName, code);
            if (!string.IsNullOrEmpty(culture.Name)) languages.TryAdd(culture.Name, code);
        }
        return languages;
    }


    private static bool TrySubtitleUri(string value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out uri!)
            && uri.Scheme is "http" or "https"
            && uri.UserInfo.Length == 0
            && uri.Fragment.Length == 0
            && !string.IsNullOrEmpty(uri.Host))
            return true;
        uri = null!;
        return false;
    }

    private static string? SubtitleFormat(Uri uri, string? mediaType)
    {
        var extension = Path.GetExtension(uri.AbsolutePath).TrimStart('.').ToLowerInvariant();
        if (extension is "srt" or "vtt" or "ass" or "ssa")
            return extension;

        return mediaType?.Split(';', 2)[0].Trim().ToLowerInvariant() switch
        {
            "text/vtt" or "text/webvtt" => "vtt",
            "application/x-subrip" or "application/srt" or "text/srt" => "srt",
            "text/x-ass" or "application/x-ass" => "ass",
            "text/x-ssa" or "application/x-ssa" => "ssa",
            _ => null
        };
    }

    private static string SafeDisplay(string value)
    {
        if (value.Length > 200 || value.Contains(':') || value.IndexOfAny(['/', '\\', '?', '=', '@', '%', '<', '>']) >= 0
            || value.Any(char.IsControl) || value.Contains("token", StringComparison.OrdinalIgnoreCase)
            || value.Contains("authorization", StringComparison.OrdinalIgnoreCase)) return "Siphon subtitles";
        return string.IsNullOrWhiteSpace(value) ? "Siphon subtitles" : value.Trim();
    }

    private CancellationTokenSource CreateTimeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_configuration.Current.AddonTimeoutSeconds, 1, 120)));
        return timeout;
    }

    private static async Task<byte[]?> ReadBoundedAsync(HttpResponseMessage response, int maximum, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is { } contentLength && contentLength > maximum)
            return null;

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (output.Length + read > maximum)
                return null;
            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private static bool Text(JsonElement element, string name, out string value)
    {
        if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && property.GetString() is { } text)
        {
            value = text.Trim();
            return value.Length > 0;
        }

        value = string.Empty;
        return false;
    }

    private sealed record SubtitleWire(string Id, string Url, string Language);

    public void Dispose() => _requests.Dispose();
}
