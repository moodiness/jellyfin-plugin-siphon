using System.Net;
using System.Net.Sockets;
using System.Text;
using Jellyfin.Plugin.Siphon.Configuration;

namespace Jellyfin.Plugin.Siphon.Infrastructure;

public sealed class SafeHttpClient : ISafeHttpClient, IDisposable
{
    private readonly ConfigurationAccessor _configuration;
    private readonly SsrfPolicy _policy;
    private readonly HttpClient _client;
    private readonly HttpClient _identityClient;
    private readonly HttpClient _privateClient;
    private readonly HttpClient _privateIdentityClient;
    private static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Content-Length", "Transfer-Encoding", "TE", "Trailer", "Upgrade",
        "Proxy-Authorization", "Proxy-Connection", "Keep-Alive"
    };

    public SafeHttpClient(ConfigurationAccessor configuration, SsrfPolicy policy)
    {
        _configuration = configuration;
        _policy = policy;
        _client = CreateClient(DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli);
        _identityClient = CreateClient(DecompressionMethods.None);
        _privateClient = CreateClient(DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli, allowPooling: false);
        _privateIdentityClient = CreateClient(DecompressionMethods.None, allowPooling: false);
    }

    private HttpClient CreateClient(DecompressionMethods decompression, bool allowPooling = true) => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = decompression,
        ConnectCallback = (context, ct) => ConnectAsync(context, allowPrivateHost: !allowPooling, ct),
        PooledConnectionLifetime = allowPooling ? TimeSpan.FromMinutes(2) : TimeSpan.Zero,
        MaxResponseHeadersLength = 64
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    public Task<HttpResponseMessage> SendAsync(Uri uri, HttpMethod method,
        IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
    {
        if (method != HttpMethod.Get && method != HttpMethod.Head)
        {
            throw new ArgumentException("Only GET and HEAD are supported.", nameof(method));
        }
        return SendCoreAsync(uri, method, headers, null, cancellationToken);
    }

    /// <summary>Sends bounded provider authentication JSON without following redirects.</summary>
    public Task<HttpResponseMessage> PostJsonAsync(Uri uri, string json,
        IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > 32 * 1024)
            throw new ArgumentException("Authentication payload exceeds the size limit.", nameof(json));
        return SendCoreAsync(uri, HttpMethod.Post, headers, json, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendCoreAsync(Uri uri, HttpMethod method,
        IReadOnlyDictionary<string, string>? headers, string? json, CancellationToken cancellationToken)
    {

        var forwarded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                if (string.IsNullOrEmpty(name) || name.Any(c => !IsHeaderCharacter(c))
                    || value.Any(c => c is '\r' or '\n' or '\0' || (c < 32 && c != '\t')))
                {
                    throw new HttpRequestException("Invalid upstream header.");
                }

                if (!ForbiddenHeaders.Contains(name))
                {
                    forwarded[name] = value;
                }
            }
        }

        // Media ranges must remain byte-for-byte representations. If the origin ignores
        // identity, leave its encoding visible so the proxy can reject it instead of
        // silently combining decompressed bytes with compressed Content-Range offsets.
        var preserveBytes = forwarded.TryGetValue("Accept-Encoding", out var encoding)
            && encoding.Equals("identity", StringComparison.OrdinalIgnoreCase);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_configuration.Current.AddonTimeoutSeconds, 1, 120)));
        for (var redirects = 0; ; redirects++)
        {
            _policy.ValidateUri(uri);
            using var request = new HttpRequestMessage(method, uri);
            if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            request.Headers.UserAgent.ParseAdd("Siphon/12.1");
            if (preserveBytes) request.Headers.AcceptEncoding.ParseAdd("identity");
            foreach (var (name, value) in forwarded)
            {
                request.Headers.Remove(name);
                if (!request.Headers.TryAddWithoutValidation(name, value))
                {
                    throw new HttpRequestException("Unsupported upstream header.");
                }
            }
            // A peer can ignore Connection: close. Enforce private-host revocation
            // locally by never pooling sockets approved through an allowlist exception.
            var privateHost = _policy.AllowsPrivateHost(uri.IdnHost);
            var client = privateHost
                ? (preserveBytes ? _privateIdentityClient : _privateClient)
                : (preserveBytes ? _identityClient : _client);

            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect
                or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
            {
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (json is not null)
            {
                // Login bodies contain credentials: even same-origin redirects are not replayed.
                throw new HttpRequestException("Authentication redirects are not permitted.");
            }
            if (location is null || redirects >= 5)
            {
                throw new HttpRequestException("Invalid or excessive upstream redirects.");
            }

            var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
            _policy.ValidateUri(next);
            if (uri.Scheme == Uri.UriSchemeHttps && next.Scheme != Uri.UriSchemeHttps)
            {
                throw new HttpRequestException("HTTPS downgrade redirect refused.");
            }

            if (!SameOrigin(uri, next))
            {
                // Custom headers can contain credentials too: cross-origin redirects receive
                // only representation/range headers, never addon secrets or referrer URLs.
                foreach (var name in forwarded.Keys.ToArray())
                {
                    if (!name.Equals("Range", StringComparison.OrdinalIgnoreCase)
                        && !name.Equals("Accept", StringComparison.OrdinalIgnoreCase)
                        && !name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
                    {
                        forwarded.Remove(name);
                    }
                }
            }

            uri = next;
        }
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, bool allowPrivateHost, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        // A pooled connection must remain public-only even if configuration changes
        // between transport selection and DNS resolution. Refuse mixed DNS answers.
        var privateAllowed = allowPrivateHost && _policy.AllowsPrivateHost(host);
        if (addresses.Length == 0 || (!privateAllowed && addresses.Any(address => !SsrfPolicy.IsPublicAddress(address))))
        {
            throw new HttpRequestException("Upstream destination is not permitted.");
        }

        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException)
            {
                socket.Dispose();
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new HttpRequestException("Unable to connect to upstream destination.");
    }

    private static bool SameOrigin(Uri first, Uri second) => first.Scheme == second.Scheme
        && first.IdnHost.Equals(second.IdnHost, StringComparison.OrdinalIgnoreCase) && first.Port == second.Port;

    private static bool IsHeaderCharacter(char c) => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c);

    public void Dispose()
    {
        _client.Dispose();
        _identityClient.Dispose();
        _privateClient.Dispose();
        _privateIdentityClient.Dispose();
    }
}
