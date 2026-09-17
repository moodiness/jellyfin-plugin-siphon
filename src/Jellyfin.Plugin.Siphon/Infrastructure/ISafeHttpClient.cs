namespace Jellyfin.Plugin.Siphon.Infrastructure;

/// <summary>Streams a response after validating every redirect and the connected IP address.</summary>
/// <remarks>Explicit identity encoding preserves the origin's bytes and encoding headers; callers enforce body read limits.</remarks>
public interface ISafeHttpClient
{
    Task<HttpResponseMessage> SendAsync(
        Uri uri,
        HttpMethod method,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken);
}
