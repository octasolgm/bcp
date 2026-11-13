using System.Net;
using System.Net.Sockets;

namespace Reguliq.Api.Infrastructure;

/// <summary>
/// Minimal SSRF guard for server-side fetches of user/admin-supplied URLs. Resolves the host and rejects it
/// if any resolved address is loopback, private, link-local, carrier-grade-NAT or otherwise non-public, so a
/// fetch cannot be pointed at internal services or cloud metadata endpoints.
///
/// Note: this checks DNS at validation time; it does not pin the connection to the resolved address, so it is
/// a proportionate mitigation (the fetch here is https-only and admin-only), not a defence against a
/// determined DNS-rebinding attacker. For stronger guarantees, pin the socket to a vetted IP.
/// </summary>
public static class SsrfGuard
{
    public static async Task<bool> IsPublicHostAsync(string host, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(host, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(host, ct);
        }
        catch
        {
            return false;
        }

        return addresses.Length > 0 && addresses.All(IsPublic);
    }

    private static bool IsPublic(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
            return false;

        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 0 || b[0] == 10 || b[0] == 127) return false;          // this-network, private, loopback
            if (b[0] == 169 && b[1] == 254) return false;                       // link-local (incl. cloud metadata)
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;          // private
            if (b[0] == 192 && b[1] == 168) return false;                       // private
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false;         // carrier-grade NAT
            if (b[0] == 192 && b[1] == 0 && b[2] == 0) return false;            // IETF protocol assignments
            if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return false;        // benchmarking
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal || ip.IsIPv6Multicast)
                return false;
            return true;
        }

        return false;
    }
}
