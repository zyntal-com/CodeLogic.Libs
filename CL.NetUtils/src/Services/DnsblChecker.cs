using System.Net;
using System.Net.Sockets;
using CL.NetUtils.Localization;
using CL.NetUtils.Models;
using CodeLogic.Core.Logging;

namespace CL.NetUtils.Services;

/// <summary>
/// Checks IP addresses against DNS-based blacklists (DNSBLs).
/// Supports both IPv4 and IPv6 addresses, primary and fallback service lists.
/// </summary>
public partial class DnsblChecker
{
    private readonly DnsblConfig _config;
    private readonly ILogger? _logger;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    private readonly bool _enabled;

    /// <summary>
    /// Initializes a new <see cref="DnsblChecker"/>.
    /// </summary>
    /// <param name="config">DNSBL configuration.</param>
    /// <param name="logger">Optional logger; pass <see langword="null"/> to suppress logging.</param>
    public DnsblChecker(DnsblConfig config, ILogger? logger = null)
        : this(config, logger, static (host, ct) => Dns.GetHostAddressesAsync(host, ct))
    {
    }

    /// <summary>Test seam: resolves DNSBL names through <paramref name="resolve"/> instead of the system resolver.</summary>
    internal DnsblChecker(DnsblConfig config, ILogger? logger, Func<string, CancellationToken, Task<IPAddress[]>> resolve, bool enabled = true)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;
        _resolve = resolve;
        _enabled = enabled;
    }

    /// <summary>Supplies the (localized) messages; set by <see cref="NetUtilsLibrary"/>.</summary>
    internal Func<NetUtilsStrings> Strings { get; set; } = static () => InputText.DefaultStrings;
}
