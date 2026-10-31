namespace CL.NetUtils.Models;

/// <summary>
/// Per-call DNSBL check parameters. Lets callers supply their own service
/// lists and an allowlist predicate, overriding the defaults loaded from
/// <see cref="NetUtilsConfig"/>. Useful for apps that manage DNSBL services
/// and IP allowlists in their own database rather than the library config.
/// </summary>
public sealed class DnsblCheckRequest
{
    /// <summary>Primary DNSBL zones to query for IPv4 addresses.</summary>
    public IReadOnlyList<string> Ipv4Services { get; init; } = [];

    /// <summary>Fallback DNSBL zones for IPv4 (queried only if primaries don't match).</summary>
    public IReadOnlyList<string> Ipv4FallbackServices { get; init; } = [];

    /// <summary>Primary DNSBL zones to query for IPv6 addresses.</summary>
    public IReadOnlyList<string> Ipv6Services { get; init; } = [];

    /// <summary>Fallback DNSBL zones for IPv6.</summary>
    public IReadOnlyList<string> Ipv6FallbackServices { get; init; } = [];

    /// <summary>
    /// Optional async predicate: return true to bypass all DNSBL queries for
    /// the given IP (e.g. because it is on an app-managed allowlist). Returning
    /// false (or leaving this null) means "proceed with DNSBL checks".
    /// </summary>
    public Func<string, CancellationToken, Task<bool>>? IsAllowedAsync { get; init; }

    /// <summary>
    /// Query the zones of a list (primaries, then fallbacks) concurrently. When <see langword="false"/>, zones are
    /// queried one at a time and the check stops at the first listing. Defaults to <see langword="true"/>.
    /// </summary>
    public bool ParallelQueries { get; init; } = true;

    /// <summary>DNS query timeout per zone. Defaults to 5s.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Emit per-zone debug logs for each lookup.</summary>
    public bool DetailedLogging { get; init; }
}
