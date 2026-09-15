namespace Jellyfin.Plugin.Siphon.Infrastructure;

/// <summary>Streams a response after validating every redirect and the connected IP address.</summary>
public interface ISafeHttpClient
{
    Task<HttpResponseMessage> SendAsync(
        Uri uri,
        HttpMethod method,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken);
}
