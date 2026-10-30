using CL.NetUtils;
using CodeLogic.Core.Events;
using CodeLogic.Framework.Libraries;
using CL.NetUtils.Models;
using CL.NetUtils.Services;
using Xunit;

namespace NetUtils.Tests;

// ── CL.NetUtils tests ─────────────────────────────────────────────────────────────
// HYBRID strategy:
//   • The result models / factory methods and the IP-type enum are pure, in-memory
//     values — exercised directly with no network or external file.
//   • The DnsblChecker's local/private-IP short-circuit is a real behavior that runs
//     entirely offline (it returns "not blacklisted" before touching DNS), so it is
//     asserted directly.
//   • Live DNSBL lookups need DNS, and GeoIP lookups need a MaxMind .mmdb file, so
//     those two tests are env-gated and SKIP unless their opt-in variables are set.

// ── DnsblCheckResult factory methods (offline) ────────────────────────────────────

public sealed class DnsblCheckResultTests
{
    [Fact]
    public void Blacklisted_sets_flag_and_matched_service()
    {
        var r = DnsblCheckResult.Blacklisted("1.2.3.4", IpAddressType.IPv4, "zen.spamhaus.org");

        Assert.True(r.IsBlacklisted);
        Assert.Equal("zen.spamhaus.org", r.MatchedService);
        Assert.Equal("1.2.3.4", r.IpAddress);
        Assert.Equal(IpAddressType.IPv4, r.AddressType);
        Assert.Null(r.ErrorMessage);
    }

    [Fact]
    public void NotBlacklisted_clears_flag_and_service()
    {
        var r = DnsblCheckResult.NotBlacklisted("1.2.3.4", IpAddressType.IPv4);

        Assert.False(r.IsBlacklisted);
        Assert.Null(r.MatchedService);
        Assert.Null(r.ErrorMessage);
        Assert.Equal(IpAddressType.IPv4, r.AddressType);
    }

    [Fact]
    public void Error_carries_error_message_and_is_not_blacklisted()
    {
        var r = DnsblCheckResult.Error("bogus", IpAddressType.Unknown, "Invalid IP address format");

        Assert.False(r.IsBlacklisted);
        Assert.Equal("Invalid IP address format", r.ErrorMessage);
        Assert.Equal(IpAddressType.Unknown, r.AddressType);
    }
}

// ── IpLocationResult factory methods + IsSuccessful (offline) ──────────────────────

public sealed class IpLocationResultTests
{
    [Fact]
    public void NotFound_is_not_successful_and_has_error()
    {
        var r = IpLocationResult.NotFound("8.8.8.8");

        Assert.False(r.IsSuccessful);
        Assert.False(string.IsNullOrEmpty(r.ErrorMessage));
        Assert.Equal("8.8.8.8", r.IpAddress);
    }

    [Fact]
    public void Error_is_not_successful_and_carries_message()
    {
        var r = IpLocationResult.Error("8.8.8.8", "boom");

        Assert.False(r.IsSuccessful);
        Assert.Equal("boom", r.ErrorMessage);
    }

    [Fact]
    public void Manually_built_success_is_successful()
    {
        var r = new IpLocationResult
        {
            IpAddress = "8.8.8.8",
            CountryCode = "US",
            CountryName = "United States",
            ErrorMessage = null
        };

        Assert.True(r.IsSuccessful);
        Assert.Equal("US", r.CountryCode);
    }

    [Fact]
    public void Missing_country_code_is_not_successful()
    {
        var r = new IpLocationResult { IpAddress = "8.8.8.8" };

        Assert.False(r.IsSuccessful);
    }
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
        var result = await NewChecker().CheckIpAsync(ip, CancellationToken.None);

        Assert.False(result.IsBlacklisted);
        Assert.Null(result.ErrorMessage);
        Assert.Equal(ip, result.IpAddress);
        Assert.Equal(expectedType, result.AddressType);
    }

    [Fact]
    public async Task Ipv6_loopback_short_circuits_to_not_blacklisted()
    {
        var result = await NewChecker().CheckIpAsync("::1", CancellationToken.None);

        Assert.False(result.IsBlacklisted);
        Assert.Null(result.ErrorMessage);
        Assert.Equal(IpAddressType.IPv6, result.AddressType);
    }

    [Fact]
    public async Task Invalid_ip_returns_error_result()
    {
        var result = await NewChecker().CheckIpAsync("not-an-ip", CancellationToken.None);

        Assert.False(result.IsBlacklisted);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
        Assert.Equal(IpAddressType.Unknown, result.AddressType);
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
        var result = await checker.CheckIpAsync("8.8.8.8", CancellationToken.None);

        Assert.Null(result.ErrorMessage);
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

        var result = await service.LookupIpAsync("8.8.8.8", CancellationToken.None);

        Assert.True(result.IsSuccessful, result.ErrorMessage);
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
