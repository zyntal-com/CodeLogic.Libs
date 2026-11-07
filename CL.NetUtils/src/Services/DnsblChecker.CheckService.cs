using System.Net;
using System.Net.Sockets;
using CL.NetUtils.Localization;
using CL.NetUtils.Models;
using CodeLogic.Core.Logging;

namespace CL.NetUtils.Services;

public partial class DnsblChecker
{

    private async Task<ZoneOutcome> CheckServiceAsync(
        string lookup,
        string service,
        TimeSpan timeout,
        bool detailedLogging,
        CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);
            addresses = await _resolve(lookup, linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            if (detailedLogging)
                _logger?.Debug($"DNS lookup timeout for {lookup}");
            return ZoneOutcome.Failed;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData)
        {
            // NXDOMAIN / no A record: the zone answered, and the address is not listed.
            return ZoneOutcome.NotListed;
        }
        catch (Exception ex)
        {
            if (detailedLogging)
                _logger?.Debug($"DNS lookup failed for {lookup}: {ex.Message}");
            return ZoneOutcome.Failed;
        }

        var sawError = false;
        foreach (var answer in addresses)
        {
            // DNSBL convention: a listing is an A record inside 127.0.0.0/8.
            // Anything else is a wildcarded/parked domain answering for a dead
            // zone — treating it as a listing would flag every IP checked.
            var bytes = answer.AddressFamily == AddressFamily.InterNetwork ? answer.GetAddressBytes() : null;
            if (bytes is null || bytes[0] != 127)
            {
                if (detailedLogging)
                    _logger?.Debug($"Ignoring non-127/8 DNSBL answer {answer} from {service}");
                continue;
            }

            // 127.255.255.0/24 is the Spamhaus error range (open resolver,
            // query volume exceeded, typing error) — a service-side problem,
            // not a listing, and the zone was not actually checked.
            if (bytes[1] == 255 && bytes[2] == 255)
            {
                _logger?.Warning(
                    $"DNSBL {service} returned error code {answer} (public resolver blocked / query limit / config error) — " +
                    "results from this zone are unusable until the resolver setup is fixed.");
                sawError = true;
                continue;
            }

            return ZoneOutcome.Listed;
        }

        return sawError ? ZoneOutcome.Failed : ZoneOutcome.NotListed;
    }

    /// <summary>
    /// Whether an address is not publicly routable, so no DNSBL can list it: loopback, private, link-local,
    /// shared (CGNAT), documentation/benchmark, multicast, reserved and unspecified ranges.
    /// </summary>
    internal static bool IsLocalOrPrivateIp(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (IPAddress.IsLoopback(ip))
            return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] switch
            {
                0 => true,                                   // 0.0.0.0/8 "this network"
                10 => true,                                  // 10.0.0.0/8 private
                100 => b[1] is >= 64 and <= 127,             // 100.64.0.0/10 shared address space (CGNAT)
                169 => b[1] == 254,                          // 169.254.0.0/16 link-local
                172 => b[1] is >= 16 and <= 31,              // 172.16.0.0/12 private
                192 => (b[1] == 168)                         // 192.168.0.0/16 private
                       || (b[1] == 0 && b[2] is 0 or 2),     // 192.0.0.0/24 IETF, 192.0.2.0/24 TEST-NET-1
                198 => b[1] is 18 or 19                      // 198.18.0.0/15 benchmarking
                       || (b[1] == 51 && b[2] == 100),       // 198.51.100.0/24 TEST-NET-2
                203 => b[1] == 0 && b[2] == 113,             // 203.0.113.0/24 TEST-NET-3
                >= 224 => true,                              // 224.0.0.0/4 multicast, 240.0.0.0/4 reserved, broadcast
                _ => false
            };
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.Equals(IPAddress.IPv6None) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast
                || ip.IsIPv6UniqueLocal)
                return true;

            var b = ip.GetAddressBytes();
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) return true;   // 2001:db8::/32 documentation
        }

        return false;
    }

    /// <summary>
    /// The DNSBL query label for an address, built from its bytes (not the input text, which may be a shorthand
    /// such as <c>127.1</c> or <c>0x7f.0.0.1</c>): reversed octets for IPv4, reversed nibbles for IPv6.
    /// </summary>
    internal static string ReverseLookupLabel(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
            return $"{bytes[3]}.{bytes[2]}.{bytes[1]}.{bytes[0]}";

        var nibbles = bytes
            .SelectMany(b => new[] { (b >> 4) & 0xF, b & 0xF })
            .Reverse();
        return string.Join(".", nibbles.Select(n => n.ToString("x")));
    }

    private static IpAddressType GetAddressType(IPAddress ip) =>
        ip.AddressFamily switch
        {
            AddressFamily.InterNetwork => IpAddressType.IPv4,
            AddressFamily.InterNetworkV6 => IpAddressType.IPv6,
            _ => IpAddressType.Unknown
        };
}
