using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using CodeLogic.Core.Results;

namespace CL.Common.Networking;

/// <summary>
/// A successful ICMP echo reply. A ping that gets no reply is a failed
/// <see cref="Result{T}"/> (<see cref="TimeoutError"/> or <see cref="UnavailableError"/>), never a value of this type.
/// </summary>
/// <param name="Host">The hostname or IP address that was pinged.</param>
/// <param name="Address">The IP address that answered.</param>
/// <param name="RoundTripMs">Round-trip time in milliseconds.</param>
public sealed record PingResponse(string Host, string Address, long RoundTripMs);

/// <summary>
/// Calculated information about a subnet.
/// </summary>
/// <param name="NetworkAddress">The network address (host bits zeroed).</param>
/// <param name="BroadcastAddress">The broadcast address (host bits all set).</param>
/// <param name="SubnetMask">The subnet mask as provided.</param>
/// <param name="UsableHosts">The number of usable host addresses in the subnet.</param>
public record SubnetInfo(string NetworkAddress, string BroadcastAddress, string SubnetMask, int UsableHosts);

/// <summary>
/// Represents a single hop in a traceroute path.
/// </summary>
/// <param name="Hop">The hop number (1-based).</param>
/// <param name="Address">The IP address or hostname of this hop, or "*" if it did not respond.</param>
/// <param name="Ms">Round-trip time in milliseconds to this hop, or -1 if it timed out.</param>
public record HopInfo(int Hop, string Address, long Ms);

/// <summary>
/// Provides asynchronous network ping operations using ICMP.
/// </summary>
public static class NetworkPing
{
    /// <summary>
    /// Sends an ICMP echo (ping) request to the specified host.
    /// </summary>
    /// <param name="host">The hostname or IP address to ping. Must not be null or empty.</param>
    /// <param name="timeout">Timeout in milliseconds to wait for a reply. Default: 3000.</param>
    /// <returns>
    /// A <see cref="Result{T}"/> containing a <see cref="PingResponse"/> when the host replied.
    /// Failures: <see cref="TimeoutError"/> <c>networking.ping_timeout</c> when no reply arrived in time;
    /// <see cref="UnavailableError"/> <c>networking.ping_unreachable</c> when the host or network is
    /// unreachable or the name cannot be resolved (the <see cref="IPStatus"/> is in <see cref="Error.Details"/>);
    /// <see cref="InternalError"/> <c>networking.ping_failed</c> for anything else.
    /// </returns>
    public static async Task<Result<PingResponse>> PingAsync(string host, int timeout = 3000)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, timeout);
            return reply.Status switch
            {
                IPStatus.Success => new PingResponse(host, reply.Address.ToString(), reply.RoundtripTime),
                IPStatus.TimedOut => Error.Timeout("networking.ping_timeout",
                    $"No ping reply from {host} within {timeout} ms"),
                _ => Error.Unavailable("networking.ping_unreachable",
                    $"Ping to {host} failed: {reply.Status}", reply.Status.ToString()),
            };
        }
        catch (PingException ex)
        {
            return Error.Unavailable("networking.ping_unreachable",
                $"Ping to {host} failed: {ex.InnerException?.Message ?? ex.Message}",
                (ex.InnerException ?? ex).GetType().Name);
        }
        catch (Exception ex)
        {
            return Error.FromException(ex, "networking.ping_failed");
        }
    }
}

/// <summary>
/// Provides asynchronous DNS lookup operations.
/// </summary>
public static class NetworkDns
{
    /// <summary>
    /// Resolves all IP addresses for the given hostname using the system DNS resolver.
    /// </summary>
    /// <param name="hostname">The hostname or domain name to resolve. Must not be null or empty.</param>
    /// <returns>
    /// A <see cref="Result{T}"/> containing an array of IP address strings on success,
    /// or a failure result if the DNS lookup fails.
    /// </returns>
    public static async Task<Result<string[]>> LookupAsync(string hostname)
    {
        ArgumentException.ThrowIfNullOrEmpty(hostname);
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(hostname);
            var result = addresses.Select(a => a.ToString()).ToArray();
            return Result<string[]>.Success(result);
        }
        catch (Exception ex)
        {
            return Result<string[]>.Failure(Error.FromException(ex, "networking.dns_lookup_failed"));
        }
    }
}

/// <summary>
/// Provides subnet calculation utilities for IPv4 addresses.
/// </summary>
public static class SubnetCalculator
{
    /// <summary>
    /// Calculates network address, broadcast address, and usable host count for the given IPv4 address and subnet mask.
    /// </summary>
    /// <param name="ipAddress">An IPv4 address in dotted-decimal notation (e.g., "192.168.1.50"). Must not be null or empty.</param>
    /// <param name="subnetMask">A subnet mask in dotted-decimal notation (e.g., "255.255.255.0"). Must not be null or empty.</param>
    /// <returns>
    /// A <see cref="Result{T}"/> containing a <see cref="SubnetInfo"/> on success,
    /// or a failure result if the inputs are invalid.
    /// </returns>
    public static Result<SubnetInfo> Calculate(string ipAddress, string subnetMask)
    {
        ArgumentException.ThrowIfNullOrEmpty(ipAddress);
        ArgumentException.ThrowIfNullOrEmpty(subnetMask);
        try
        {
            if (!IPAddress.TryParse(ipAddress, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                return Result<SubnetInfo>.Failure(Error.Validation("networking.invalid_ip", $"Invalid IPv4 address: {ipAddress}"));

            if (!IPAddress.TryParse(subnetMask, out var mask) || mask.AddressFamily != AddressFamily.InterNetwork)
                return Result<SubnetInfo>.Failure(Error.Validation("networking.invalid_mask", $"Invalid subnet mask: {subnetMask}"));

            var ipBytes   = ip.GetAddressBytes();
            var maskBytes = mask.GetAddressBytes();

            var networkBytes   = new byte[4];
            var broadcastBytes = new byte[4];
            for (int i = 0; i < 4; i++)
            {
                networkBytes[i]   = (byte)(ipBytes[i] & maskBytes[i]);
                broadcastBytes[i] = (byte)(networkBytes[i] | ~maskBytes[i]);
            }

            // Count host bits to calculate usable hosts
            uint maskInt  = BitConverter.ToUInt32(maskBytes.Reverse().ToArray(), 0);
            int  hostBits = 0;
            uint temp     = ~maskInt;
            while (temp > 0) { hostBits++; temp >>= 1; }
            int usableHosts = hostBits >= 2 ? (int)Math.Pow(2, hostBits) - 2 : 0;

            var info = new SubnetInfo(
                new IPAddress(networkBytes).ToString(),
                new IPAddress(broadcastBytes).ToString(),
                subnetMask,
                usableHosts);

            return Result<SubnetInfo>.Success(info);
        }
        catch (Exception ex)
        {
            return Result<SubnetInfo>.Failure(Error.FromException(ex, "networking.subnet_calc_failed"));
        }
    }
}

/// <summary>
/// Provides asynchronous traceroute operations using ICMP with incrementing TTL values.
/// </summary>
public static class TraceRoute
{
    /// <summary>
    /// Traces the network route to the specified host by sending ICMP echo requests with increasing TTL values.
    /// </summary>
    /// <param name="host">The hostname or IP address to trace to. Must not be null or empty.</param>
    /// <param name="maxHops">The maximum number of hops to trace. Default: 30.</param>
    /// <returns>
    /// A <see cref="Result{T}"/> containing a list of <see cref="HopInfo"/> records on success,
    /// or a failure result if an exception occurs.
    /// </returns>
    public static async Task<Result<List<HopInfo>>> TraceAsync(string host, int maxHops = 30)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        try
        {
            var hops = new List<HopInfo>();
            using var ping = new Ping();
            var options = new PingOptions { DontFragment = true };

            for (int ttl = 1; ttl <= maxHops; ttl++)
            {
                options.Ttl = ttl;
                var reply = await ping.SendPingAsync(host, 3000, new byte[32], options);

                var address = reply.Address?.ToString() ?? "*";
                var ms = reply.Status is IPStatus.Success or IPStatus.TtlExpired
                    ? reply.RoundtripTime
                    : -1L;

                hops.Add(new HopInfo(ttl, address, ms));

                if (reply.Status == IPStatus.Success)
                    break;
            }

            return Result<List<HopInfo>>.Success(hops);
        }
        catch (Exception ex)
        {
            return Result<List<HopInfo>>.Failure(Error.FromException(ex, "networking.traceroute_failed"));
        }
    }
}
