using System.Net;
using System.Net.Sockets;
using Jellyfin.Plugin.Siphon.Configuration;

namespace Jellyfin.Plugin.Siphon.Infrastructure;

public sealed class SsrfPolicy(ConfigurationAccessor configuration)
{
    public void ValidateUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo) || string.IsNullOrEmpty(uri.Host))
        {
            throw new HttpRequestException("Only HTTP(S) destinations without embedded credentials are supported.");
        }
    }

    public bool AllowsPrivateHost(string host) => configuration.Current.AllowedPrivateHosts.Any(
        allowed => string.Equals(allowed.Trim().TrimEnd('.'), host.Trim('[', ']').TrimEnd('.'), StringComparison.OrdinalIgnoreCase));

    public bool IsAllowed(string host, IPAddress address) => AllowsPrivateHost(host) || IsPublicAddress(address);

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            // IANA special-purpose networks, including documentation, benchmark and multicast space.
            return !(bytes[0] is 0 or 10 or 127 || bytes[0] >= 224
                || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                || (bytes[0] == 169 && bytes[1] == 254)
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && (bytes[1] == 168 || (bytes[1] == 0 && bytes[2] is 0 or 2)
                    || (bytes[1] == 88 && bytes[2] == 99)))
                || (bytes[0] == 198 && (bytes[1] is 18 or 19 || (bytes[1] == 51 && bytes[2] == 100)))
                || (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113));
        }

        // Restrict IPv6 to global unicast. Exclude protocol-assignment, documentation,
        // 6to4 (which embeds an unchecked IPv4 address) and SRv6 SID space.
        return address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId == 0
            && (bytes[0] & 0xe0) == 0x20
            && !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] < 0x02)
            && !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8)
            && !(bytes[0] == 0x20 && bytes[1] == 0x02)
            && !(bytes[0] == 0x3f && (bytes[1] & 0xf0) == 0xf0);
    }
}
