using System.Net;
using System.Net.Sockets;
using Jellyfin.Plugin.Siphon.Configuration;

namespace Jellyfin.Plugin.Siphon.Infrastructure;

public sealed class SafeHttpClient : ISafeHttpClient, IDisposable
{
    private readonly ConfigurationAccessor _configuration;
    private readonly SsrfPolicy _policy;
    private readonly HttpClient _client;
    private static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Content-Length", "Transfer-Encoding", "TE", "Trailer", "Upgrade",
        "Proxy-Authorization", "Proxy-Connection", "Keep-Alive"
    };

    public SafeHttpClient(ConfigurationAccessor configuration, SsrfPolicy policy)
    {
        _configuration = configuration;
        _policy = policy;
        _client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectCallback = ConnectAsync,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            MaxResponseHeadersLength = 64
        })
        { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<HttpResponseMessage> SendAsync(Uri uri, HttpMethod method,
        IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
    {
        if (method != HttpMethod.Get && method != HttpMethod.Head)
        {
            throw new ArgumentException("Only GET and HEAD are supported.", nameof(method));
        }

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

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_configuration.Current.AddonTimeoutSeconds, 1, 120)));
        for (var redirects = 0; ; redirects++)
        {
            _policy.ValidateUri(uri);
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.UserAgent.ParseAdd("Siphon/12.1");
            foreach (var (name, value) in forwarded)
            {
                request.Headers.Remove(name);
                if (!request.Headers.TryAddWithoutValidation(name, value))
                {
                    throw new HttpRequestException("Unsupported upstream header.");
                }
            }

            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect
                or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
            {
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
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

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        // Refuse mixed public/private DNS answers, not merely the particular first address.
        if (addresses.Length == 0 || addresses.Any(address => !_policy.IsAllowed(host, address)))
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

    public void Dispose() => _client.Dispose();
}
