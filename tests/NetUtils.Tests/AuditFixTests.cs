using System.Net;
using System.Net.Sockets;
using CL.NetUtils;
using CL.NetUtils.Localization;
using CL.NetUtils.Models;
using CL.NetUtils.Services;
using CodeLogic.Core.Events;
using CodeLogic.Framework.Libraries;
using Xunit;

namespace NetUtils.Tests;

// ── DNSBL: disabled checker, zone validation, localized messages ───────────────────

public sealed class DnsblDisabledAndZoneTests
{
    private const string Ip = "93.184.215.7";

    private sealed class Recorder
    {
        public List<string> Lookups { get; } = [];

        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
        {
            lock (Lookups) Lookups.Add(host);
            return Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound));
        }
    }

    [Fact]
    public async Task A_disabled_checker_returns_a_disabled_error_without_querying()
    {
        var checker = DnsblChecker.CreateDisabled(null);

        var result = await checker.CheckIpAsync(Ip);

        Assert.False(checker.IsEnabled);
        Assert.Equal(DnsblCheckStatus.Error, result.Status);
        Assert.False(result.IsBlacklisted);
        Assert.Equal(IpAddressType.IPv4, result.AddressType);
        Assert.Equal("DNSBL checking is disabled", result.ErrorMessage);
    }

    [Fact]
    public void A_checker_built_from_config_is_enabled() =>
        Assert.True(new DnsblChecker(new DnsblConfig()).IsEnabled);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("localhost")]                 // single label
    [InlineData("1.2.3.4")]                   // IP literal: the query name would be an address, not a DNS name
    [InlineData("zen spamhaus.org")]
    [InlineData("zen.spamhaus.org\r\nx.y")]
    [InlineData("-bad.example.org")]
    [InlineData("a..b")]
    public async Task Malformed_zones_are_never_queried_and_count_as_failed(string zone)
    {
        var recorder = new Recorder();
        var checker = new DnsblChecker(new DnsblConfig(), null, recorder.ResolveAsync);

        var result = await checker.CheckIpAsync(Ip, new DnsblCheckRequest { Ipv4Services = [zone], ParallelQueries = false });

        Assert.Empty(recorder.Lookups);
        Assert.Equal(DnsblCheckStatus.Indeterminate, result.Status);
        Assert.Single(result.FailedServices);
        Assert.DoesNotContain('\n', result.FailedServices[0]);
    }

    [Fact]
    public async Task Zones_are_trimmed_before_the_query()
    {
        var recorder = new Recorder();
        var checker = new DnsblChecker(new DnsblConfig(), null, recorder.ResolveAsync);

        var result = await checker.CheckIpAsync(Ip, new DnsblCheckRequest { Ipv4Services = ["  zen.spamhaus.org. \r\n"] });

        Assert.Equal(["7.215.184.93.zen.spamhaus.org"], recorder.Lookups);
        Assert.Equal(DnsblCheckStatus.NotBlacklisted, result.Status);
    }

    [Fact]
    public async Task Error_messages_come_from_the_localized_strings()
    {
        var checker = new DnsblChecker(new DnsblConfig())
        {
            Strings = () => new NetUtilsStrings { InvalidIpAddressFormat = "Ungueltige IP-Adresse" }
        };

        var result = await checker.CheckIpAsync("not-an-ip");

        Assert.Equal("Ungueltige IP-Adresse", result.ErrorMessage);
    }

    [Theory]
    [InlineData("1.2.3.4\r\nFAKE LOG LINE", "1.2.3.4??FAKE LOG LINE")]
    [InlineData(null, "(null)")]
    public void Input_is_made_safe_for_logs(string? input, string expected) =>
        Assert.Equal(expected, InputText.ForLog(input));

    [Fact]
    public void Long_input_is_cut_in_logs() =>
        Assert.Equal(64 + 3, InputText.ForLog(new string('a', 500)).Length);
}

// ── Library: Dnsbl when DNSBL checking is switched off ─────────────────────────────

public sealed class NetUtilsDisabledDnsblTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cl_netutils_dnsbl_" + Guid.NewGuid().ToString("N"));
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

    private async Task<NetUtilsLibrary> StartAsync(string json)
    {
        var libDir = Path.Combine(_root, "Libraries", "CL.NetUtils");
        Directory.CreateDirectory(libDir);
        await File.WriteAllTextAsync(Path.Combine(libDir, "config.netutils.json"), json);

        _manager = new LibraryManager(new EventBus()) { FrameworkRootPath = _root };
        await _manager.LoadLibraryAsync<NetUtilsLibrary>();
        await _manager.ConfigureAllAsync();
        await _manager.InitializeAllAsync();
        await _manager.StartAllAsync();
        return _manager.GetLibrary<NetUtilsLibrary>()!;
    }

    [Theory]
    [InlineData("""{ "enabled": true, "dnsbl": { "enabled": false }, "geoIp": { "enabled": false } }""")]
    [InlineData("""{ "enabled": false }""")]
    public async Task Dnsbl_is_available_and_reports_disabled(string json)
    {
        var lib = await StartAsync(json);

        Assert.False(lib.HasDnsbl);
        Assert.False(lib.Dnsbl.IsEnabled);
        var result = await lib.Dnsbl.CheckIpAsync("93.184.215.7");
        Assert.Equal(DnsblCheckStatus.Error, result.Status);
        Assert.Equal("DNSBL checking is disabled", result.ErrorMessage);
    }

    [Fact]
    public async Task Dnsbl_is_the_real_checker_when_enabled()
    {
        var lib = await StartAsync("""{ "enabled": true, "dnsbl": { "enabled": true }, "geoIp": { "enabled": false } }""");

        Assert.True(lib.HasDnsbl);
        Assert.True(lib.Dnsbl.IsEnabled);
    }

    [Fact]
    public void Dnsbl_throws_before_initialization() =>
        Assert.Throws<InvalidOperationException>(() => new NetUtilsLibrary().Dnsbl);
}

// ── GeoIP: other editions and DatabaseType validation ──────────────────────────────

public sealed class GeoIpEditionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cl_geoip_ed_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Theory]
    [InlineData("GeoLite2-City", "City")]
    [InlineData("GeoIP2-City", "City")]
    [InlineData("GeoIP2-City-Africa", "City")]
    [InlineData("DBIP-City-Lite", "City")]
    [InlineData("GeoLite2-Country", "Country")]
    [InlineData("GeoIP2-Country", "Country")]
    [InlineData("GeoIP2-Enterprise", "Enterprise")]
    [InlineData("GeoLite2-ASN", "Asn")]
    [InlineData("GeoIP2-ISP", "Isp")]
    [InlineData("GeoIP2-Anonymous-IP", null)]
    [InlineData("GeoIP2-Connection-Type", null)]
    [InlineData("GeoIP2-Domain", null)]
    [InlineData("", null)]
    public void Editions_are_recognized(string type, string? kind) =>
        Assert.Equal(kind, GeoIpDatabaseKinds.Detect(type)?.ToString());

    [Theory]
    [InlineData("GeoLite2-Country")]
    [InlineData("GeoLite2-ASN")]
    [InlineData("GeoIP2-ISP")]
    [InlineData("GeoIP2-Enterprise")]
    public async Task Non_city_editions_open_and_look_up(string edition)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, $"{edition}.mmdb");
        await File.WriteAllBytesAsync(path, GeoIpDownloadTests.FakeMmdb("db", edition));
        using var service = new GeoIpService(new GeoIpConfig { DatabasePath = path, DatabaseType = edition });

        await service.InitializeAsync();
        var result = await service.LookupIpAsync("8.8.8.8");

        Assert.Equal(edition, service.LoadedDatabaseType);
        Assert.Equal("Location not found", result.ErrorMessage);   // the fake database has no data; the lookup ran
    }

    [Fact]
    public async Task An_unsupported_edition_fails_initialization()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "anon.mmdb");
        await File.WriteAllBytesAsync(path, GeoIpDownloadTests.FakeMmdb("db", "GeoIP2-Anonymous-IP"));
        using var service = new GeoIpService(new GeoIpConfig { DatabasePath = path });

        await Assert.ThrowsAsync<InvalidDataException>(() => service.InitializeAsync());
        Assert.Null(service.LoadedDatabaseType);
    }

    [Fact]
    public async Task A_country_edition_downloads_when_configured()
    {
        var archive = GeoIpDownloadTests.TarGz(("GeoLite2-Country_20261031/GeoLite2-Country.mmdb", GeoIpDownloadTests.FakeMmdb("c", "GeoLite2-Country")));
        var handler = new GeoIpDownloadTests.RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
        var config = new GeoIpConfig { AccountId = 42, LicenseKey = "k", DatabaseType = "GeoLite2-Country" };
        using var service = new GeoIpService(config, null, _dir, handler);

        await service.InitializeAsync();

        Assert.Equal("https://download.maxmind.com/geoip/databases/GeoLite2-Country/download?suffix=tar.gz",
            Assert.Single(handler.Requests).RequestUri!.ToString());
        Assert.EndsWith("GeoLite2-Country.mmdb", service.DatabasePath);
        Assert.Equal("GeoLite2-Country", service.LoadedDatabaseType);
    }

    [Fact]
    public void The_default_download_url_follows_the_database_type()
    {
        var config = new GeoIpConfig { DatabaseType = "GeoLite2-ASN" };

        Assert.True(config.TryGetTrustedDownloadUri(out var uri, out _));
        Assert.Equal("https://download.maxmind.com/geoip/databases/GeoLite2-ASN/download?suffix=tar.gz", uri!.ToString());
    }

    [Theory]
    [InlineData("../../etc/x")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("")]
    [InlineData(".hidden")]
    [InlineData("GeoLite2 City")]
    public void Path_like_database_types_are_refused(string type)
    {
        var config = new GeoIpConfig { DatabaseType = type };

        Assert.False(GeoIpConfig.IsValidDatabaseType(type));
        Assert.False(new NetUtilsConfig { GeoIp = config }.Validate().IsValid);
        Assert.Throws<ArgumentException>(() => new GeoIpService(config, null, _dir));
    }

    [Fact]
    public void Asn_results_are_successful_without_a_country()
    {
        Assert.True(new IpLocationResult { IpAddress = "8.8.8.8", AutonomousSystemNumber = 15169 }.IsSuccessful);
        Assert.False(new IpLocationResult { IpAddress = "8.8.8.8", AutonomousSystemNumber = 15169, ErrorMessage = "x" }.IsSuccessful);
    }
}
