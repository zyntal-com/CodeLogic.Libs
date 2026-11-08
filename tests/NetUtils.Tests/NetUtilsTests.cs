using CL.NetUtils;
using CodeLogic.Core.Events;
using CodeLogic.Framework.Libraries;
using CL.NetUtils.Models;
using CL.NetUtils.Services;
using CodeLogic.Core.Results;
using Xunit;

namespace NetUtils.Tests;

// ── CL.NetUtils tests ─────────────────────────────────────────────────────────────
// HYBRID strategy:
//   • The DnsblCheck / IpLocation records and the IP-type enum are pure, in-memory
//     values — exercised directly with no network or external file.
//   • The DnsblChecker's local/private-IP short-circuit is a real behavior that runs
//     entirely offline (it returns "not blacklisted" before touching DNS), so it is
//     asserted directly.
//   • Live DNSBL lookups need DNS, and GeoIP lookups need a MaxMind .mmdb file, so
//     those two tests are env-gated and SKIP unless their opt-in variables are set.

// ── DnsblCheck factory methods (offline) ─────────────────────────────────────────

public sealed class DnsblCheckTests
{
    [Fact]
    public void Blacklisted_sets_the_status_and_service()
    {
        var r = DnsblCheck.Blacklisted("1.2.3.4", IpAddressType.IPv4, "zen.spamhaus.org", []);

        Assert.Equal(DnsblCheckStatus.Blacklisted, r.Status);
        Assert.True(r.IsBlacklisted);
        Assert.Equal("zen.spamhaus.org", r.MatchedService);
        Assert.Equal("1.2.3.4", r.IpAddress);
        Assert.Equal(IpAddressType.IPv4, r.AddressType);
    }

    [Fact]
    public void NotBlacklisted_has_no_service_and_no_failed_zones()
    {
        var r = DnsblCheck.NotBlacklisted("1.2.3.4", IpAddressType.IPv4);

        Assert.Equal(DnsblCheckStatus.NotBlacklisted, r.Status);
        Assert.False(r.IsBlacklisted);
        Assert.Null(r.MatchedService);
        Assert.Empty(r.FailedServices);
        Assert.Equal(IpAddressType.IPv4, r.AddressType);
    }

    [Fact]
    public void Indeterminate_is_not_blacklisted_and_lists_the_failed_zones()
    {
        var r = DnsblCheck.Indeterminate("1.2.3.4", IpAddressType.IPv4, ["a.test"]);

        Assert.Equal(DnsblCheckStatus.Indeterminate, r.Status);
        Assert.False(r.IsBlacklisted);
        Assert.Equal(["a.test"], r.FailedServices);
    }
}

// ── IpLocation data check (offline) ──────────────────────────────────────────────

public sealed class IpLocationTests
{
    [Fact]
    public void A_country_code_counts_as_data()
    {
        var r = new IpLocation { IpAddress = "8.8.8.8", CountryName = "United States", CountryCode = "US" };

        Assert.True(r.HasData);
        Assert.Equal("US", r.CountryCode);
    }

    [Fact]
    public void An_entry_without_country_asn_or_isp_has_no_data() =>
        Assert.False(new IpLocation { IpAddress = "8.8.8.8" }.HasData);
}

// ── IpAddressType enum (offline) ──────────────────────────────────────────────────

public sealed class IpAddressTypeTests
{
    [Fact]
    public void Enum_defines_expected_members()
    {
        Assert.True(Enum.IsDefined(typeof(IpAddressType), IpAddressType.IPv4));
        Assert.True(Enum.IsDefined(typeof(IpAddressType), IpAddressType.IPv6));
        Assert.True(Enum.IsDefined(typeof(IpAddressType), IpAddressType.Unknown));
    }
}

// ── DnsblChecker local/private-IP short-circuit (offline behavior) ─────────────────
// IsLocalOrPrivateIp returns true for loopback and RFC1918 ranges, so CheckIpAsync
// returns NotBlacklisted before any DNS lookup is attempted. This runs with no network.

public sealed class DnsblCheckerLocalShortCircuitTests
{
    private static DnsblChecker NewChecker() => new(new DnsblConfig(), logger: null);

    [Theory]
    [InlineData("127.0.0.1", IpAddressType.IPv4)]
    [InlineData("192.168.1.1", IpAddressType.IPv4)]
    [InlineData("10.0.0.5", IpAddressType.IPv4)]
    [InlineData("172.16.0.1", IpAddressType.IPv4)]
    public async Task Private_ipv4_short_circuits_to_not_blacklisted(string ip, IpAddressType expectedType)
    {
        var result = (await NewChecker().CheckIpAsync(ip, CancellationToken.None)).ValueOrThrow();

        Assert.Equal(DnsblCheckStatus.NotBlacklisted, result.Status);
        Assert.Equal(ip, result.IpAddress);
        Assert.Equal(expectedType, result.AddressType);
    }

    [Fact]
    public async Task Ipv6_loopback_short_circuits_to_not_blacklisted()
    {
        var result = (await NewChecker().CheckIpAsync("::1", CancellationToken.None)).ValueOrThrow();

        Assert.Equal(DnsblCheckStatus.NotBlacklisted, result.Status);
        Assert.Equal(IpAddressType.IPv6, result.AddressType);
    }

    [Theory]
    [InlineData("169.254.10.20")]      // link-local
    [InlineData("100.64.0.1")]         // CGNAT
    [InlineData("100.127.255.254")]
    [InlineData("0.0.0.0")]
    [InlineData("192.0.2.5")]          // TEST-NET-1
    [InlineData("198.51.100.7")]       // TEST-NET-2
    [InlineData("203.0.113.9")]        // TEST-NET-3
    [InlineData("198.18.0.1")]         // benchmarking
    [InlineData("224.0.0.251")]        // multicast
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("2001:db8::1")]
    [InlineData("::ffff:192.168.1.1")] // IPv4-mapped private
    [InlineData("::ffff:169.254.1.1")]
    public async Task Non_routable_addresses_skip_every_lookup(string ip)
    {
        var allowlistAsked = false;
        var request = new DnsblCheckRequest
        {
            Ipv4Services = ["dnsbl.invalid"],
            Ipv6Services = ["dnsbl.invalid"],
            IsAllowedAsync = (_, _) => { allowlistAsked = true; return Task.FromResult(true); }
        };

        var result = (await NewChecker().CheckIpAsync(ip, request)).ValueOrThrow();

        Assert.Equal(DnsblCheckStatus.NotBlacklisted, result.Status);
        Assert.False(allowlistAsked);   // short-circuited before the allowlist and any DNS query
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("100.63.255.255")]     // just below CGNAT
    [InlineData("100.128.0.0")]        // just above CGNAT
    [InlineData("169.253.1.1")]
    [InlineData("172.32.0.1")]
    [InlineData("2606:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    public void Public_addresses_are_checked(string ip)
        => Assert.False(DnsblChecker.IsLocalOrPrivateIp(System.Net.IPAddress.Parse(ip)));

    [Theory]
    [InlineData("1.2.3.4", "4.3.2.1")]
    [InlineData("1.2.3", "3.0.2.1")]               // shorthand: 1.2.0.3
    [InlineData("0x01.0x02.0x03.0x04", "4.3.2.1")] // hex octets
    [InlineData("16909060", "4.3.2.1")]            // single 32-bit number
    [InlineData("::ffff:1.2.3.4", "4.3.2.1")]      // mapped, after MapToIPv4
    public void Ipv4_lookup_label_comes_from_the_address_bytes(string input, string expected)
    {
        var ip = System.Net.IPAddress.Parse(input);
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        Assert.Equal(expected, DnsblChecker.ReverseLookupLabel(ip));
    }

    [Fact]
    public void Ipv6_lookup_label_is_the_reversed_nibbles()
        => Assert.Equal(
            "1.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.8.b.d.0.1.0.0.2",
            DnsblChecker.ReverseLookupLabel(System.Net.IPAddress.Parse("2001:db8::1")));

    [Fact]
    public async Task Invalid_ip_returns_a_validation_error()
    {
        var result = await NewChecker().CheckIpAsync("not-an-ip", CancellationToken.None);

        Assert.True(result.IsFailure);
        var error = Assert.IsType<ValidationError>(result.Error);
        Assert.Equal(NetUtilsErrorCodes.InvalidIpAddress, error.Code);
        Assert.False(string.IsNullOrEmpty(error.Message));
    }
}

// ── Env-gating attribute ──────────────────────────────────────────────────────────
// xUnit 2.9.3 has no runtime Assert.Skip, so the skip decision is made here (at
// discovery time) and reported as a proper "Skipped".

internal sealed class FactRequiresEnvAttribute : FactAttribute
{
    public FactRequiresEnvAttribute(string envVar, string reason)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envVar)))
            Skip = reason;
    }
}

// ── Gated live DNSBL test (needs network/DNS) ─────────────────────────────────────

public sealed class LiveDnsblTests
{
    [FactRequiresEnv("CL_NETUTILS_TEST_DNSBL", "set CL_NETUTILS_TEST_DNSBL to run live DNSBL test")]
    public async Task Public_ip_is_checked_without_error()
    {
        var checker = new DnsblChecker(new DnsblConfig(), logger: null);

        // 8.8.8.8 (Google public DNS) is a clean public IP — expected NOT blacklisted.
        var result = (await checker.CheckIpAsync("8.8.8.8", CancellationToken.None)).ValueOrThrow();

        Assert.False(result.IsBlacklisted);
        Assert.Equal(IpAddressType.IPv4, result.AddressType);
    }
}

// ── Gated live GeoIP test (needs a GeoLite2-City.mmdb file) ────────────────────────

public sealed class LiveGeoIpTests
{
    [FactRequiresEnv("CL_NETUTILS_TEST_MMDB", "set CL_NETUTILS_TEST_MMDB to a GeoLite2-City.mmdb path to run live GeoIP test")]
    public async Task Lookup_resolves_country_for_public_ip()
    {
        var mmdb = Environment.GetEnvironmentVariable("CL_NETUTILS_TEST_MMDB")!;

        var config = new GeoIpConfig { DatabasePath = mmdb };
        using var service = new GeoIpService(config, logger: null, dataDirectory: "");

        await service.InitializeAsync(CancellationToken.None);

        var result = (await service.LookupIpAsync("8.8.8.8", CancellationToken.None)).ValueOrThrow();

        Assert.False(string.IsNullOrEmpty(result.CountryCode));
    }
}

// ── Library health (offline: DNSBL is never queried, GeoIP never downloads) ───────

public sealed class NetUtilsHealthTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cl_netutils_health_" + Guid.NewGuid().ToString("N"));
    private LibraryManager? _manager;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_manager is not null)
        {
            await _manager.StopAllAsync();
            _manager.Dispose();
        }
        try { Directory.Delete(_root, recursive: true); } catch { /* logger may still hold its file */ }
    }

    private async Task<HealthStatus> StartAsync(string geoIpJson)
    {
        var libDir = Path.Combine(_root, "Libraries", "CL.NetUtils");
        Directory.CreateDirectory(libDir);
        await File.WriteAllTextAsync(Path.Combine(libDir, "config.netutils.json"),
            $$"""{ "enabled": true, "dnsbl": { "enabled": true }, "geoIp": {{geoIpJson}} }""");

        _manager = new LibraryManager(new EventBus()) { FrameworkRootPath = _root };
        await _manager.LoadLibraryAsync<NetUtilsLibrary>();
        await _manager.ConfigureAllAsync();
        await _manager.InitializeAllAsync();
        await _manager.StartAllAsync();
        return await _manager.GetLibrary<NetUtilsLibrary>()!.HealthCheckAsync();
    }

    [Fact]
    public async Task GeoIP_that_was_never_set_up_is_healthy()
    {
        var health = await StartAsync("""{ "enabled": true }""");

        Assert.Equal(HealthStatusLevel.Healthy, health.Status);
        Assert.Equal("DNSBL on, GeoIP not configured", health.Message);
        Assert.False(_manager!.GetLibrary<NetUtilsLibrary>()!.HasGeoIp);
    }

    [Fact]
    public async Task GeoIP_that_is_set_up_but_fails_degrades()
    {
        var database = Path.Combine(_root, "broken.mmdb");
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(database, "not a MaxMind database");

        var health = await StartAsync($$"""{ "enabled": true, "databasePath": {{System.Text.Json.JsonSerializer.Serialize(database)}} }""");

        Assert.Equal(HealthStatusLevel.Degraded, health.Status);
        Assert.StartsWith("DNSBL on, GeoIP failed: ", health.Message);
    }
}

// ── Error results: unexpected failures and a GeoIP service without a database (offline) ─

public sealed class NetUtilsErrorResultTests
{
    [Fact]
    public async Task An_unexpected_exception_is_an_internal_error_without_the_exception_text()
    {
        var checker = new DnsblChecker(new DnsblConfig(), logger: null);
        var request = new DnsblCheckRequest
        {
            Ipv4Services = ["a.test"],
            IsAllowedAsync = (_, _) => throw new InvalidOperationException("secret connection string")
        };

        var result = await checker.CheckIpAsync("93.184.215.7", request);

        var error = Assert.IsType<InternalError>(result.Error);
        Assert.Equal(NetUtilsErrorCodes.DnsblCheckFailed, error.Code);
        Assert.Equal("The DNSBL check failed", error.Message);
        Assert.Equal(nameof(InvalidOperationException), error.Details);
        Assert.DoesNotContain("secret", error.ToString());
    }

    [Fact]
    public async Task A_lookup_before_initialization_is_unavailable()
    {
        using var service = new GeoIpService(new GeoIpConfig { DatabasePath = "missing.mmdb" });

        var result = await service.LookupIpAsync("8.8.8.8");

        var error = Assert.IsType<UnavailableError>(result.Error);
        Assert.Equal(NetUtilsErrorCodes.GeoIpUnavailable, error.Code);
    }

    [Fact]
    public async Task A_lookup_of_text_that_is_not_an_address_is_a_validation_error()
    {
        using var service = new GeoIpService(new GeoIpConfig { DatabasePath = "missing.mmdb" });

        var result = await service.LookupIpAsync("nope");

        var error = Assert.IsType<ValidationError>(result.Error);
        Assert.Equal(NetUtilsErrorCodes.InvalidIpAddress, error.Code);
    }
}
