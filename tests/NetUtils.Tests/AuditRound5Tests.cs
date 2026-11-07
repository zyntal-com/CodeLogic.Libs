using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using CL.NetUtils;
using CL.NetUtils.Models;
using CL.NetUtils.Services;
using CodeLogic.Core.Events;
using CodeLogic.Framework.Libraries;
using Xunit;
using static NetUtils.Tests.GeoIpDownloadTests;

namespace NetUtils.Tests;

// ── GeoIP download: size cap, checksum, whole-download timeout ─────────────────────

public sealed class GeoIpDownloadLimitTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cl_netutils_limits_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private const string ArchiveUrl = "https://download.maxmind.com/geoip/databases/GeoLite2-City/download?suffix=tar.gz";
    private const string ChecksumUrl = "https://download.maxmind.com/geoip/databases/GeoLite2-City/download?suffix=tar.gz.sha256";

    private static GeoIpConfig Config(long? maxBytes = null, bool verifyChecksum = false, int timeoutSeconds = 30) => new()
    {
        AccountId = 42,
        LicenseKey = "secret-key",
        DownloadUrl = ArchiveUrl,
        TrustedDownloadHosts = ["download.maxmind.com"],
        MaxDownloadBytes = maxBytes ?? GeoIpConfig.DefaultMaxDownloadBytes,
        VerifyChecksum = verifyChecksum,
        TimeoutSeconds = timeoutSeconds
    };

    private static string Sha256Hex(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>A body of unknown length, served in small chunks: the bound must come from counting, not Content-Length.</summary>
    private sealed class ChunkedContent(byte[] data, int chunk = 1024) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            for (var offset = 0; offset < data.Length; offset += chunk)
            {
                await stream.WriteAsync(data.AsMemory(offset, Math.Min(chunk, data.Length - offset)));
                await stream.FlushAsync();
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>
    /// A body that never arrives: the download must give up on its own clock. The stall sits in the read stream's
    /// <c>ReadAsync</c> (which gets the download's token), as a real socket stream would; <see cref="HttpContent"/>'s
    /// buffered path drops the token, so the content must hand out the stream itself.
    /// </summary>
    private sealed class StalledContent : HttpContent
    {
        private sealed class StalledStream : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new StalledStream());
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => Task.FromResult<Stream>(new StalledStream());
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => Task.Delay(Timeout.Infinite);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private async Task<string> ExistingDatabaseAsync(GeoIpService service)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(service.DatabasePath)!);
        await File.WriteAllBytesAsync(service.DatabasePath, FakeMmdb("v1"));
        return service.DatabasePath;
    }

    [Fact]
    public void Default_limits_are_as_documented()
    {
        var config = new GeoIpConfig();
        Assert.Equal(512L * 1024 * 1024, config.MaxDownloadBytes);
        Assert.Equal(120, config.TimeoutSeconds);
        Assert.True(config.VerifyChecksum);
        Assert.True(new NetUtilsConfig().Validate().IsValid);
        Assert.False(new NetUtilsConfig { GeoIp = new GeoIpConfig { MaxDownloadBytes = 0 } }.Validate().IsValid);
        Assert.False(new NetUtilsConfig { GeoIp = new GeoIpConfig { TimeoutSeconds = 0 } }.Validate().IsValid);
    }

    [Fact]
    public void Checksum_url_appends_sha256_to_the_suffix_or_the_path()
    {
        Assert.Equal(ChecksumUrl, GeoIpService.ChecksumUri(new Uri(ArchiveUrl)).ToString());
        Assert.Equal("https://mirror.internal/geo/GeoLite2-City.tar.gz.sha256?x=1",
            GeoIpService.ChecksumUri(new Uri("https://mirror.internal/geo/GeoLite2-City.tar.gz?x=1")).ToString());
        Assert.Equal("https://mirror.internal/geo/GeoLite2-City.tar.gz.sha256",
            GeoIpService.ChecksumUri(new Uri("https://mirror.internal/geo/GeoLite2-City.tar.gz")).ToString());
    }

    [Fact]
    public async Task An_oversized_body_announced_by_content_length_is_refused_before_it_is_read()
    {
        var archive = TarGz(("x/GeoLite2-City.mmdb", FakeMmdb("v2")));
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
        using var service = new GeoIpService(Config(maxBytes: archive.Length - 1), null, _dir, handler);
        await ExistingDatabaseAsync(service);

        var result = await service.DownloadDatabaseAsync();

        Assert.True(result.IsFailure);
        Assert.Equal(NetUtilsErrors.DownloadTooLargeCode, result.Error!.Code);
        Assert.Equal(FakeMmdb("v1"), await File.ReadAllBytesAsync(service.DatabasePath));
        Assert.Equal([service.DatabasePath], Directory.GetFileSystemEntries(Path.GetDirectoryName(service.DatabasePath)!));
    }

    [Fact]
    public async Task An_oversized_body_of_unknown_length_is_cut_off_while_it_streams()
    {
        var filler = new byte[64 * 1024];
        RandomNumberGenerator.Fill(filler);                                 // incompressible, so the archive is large
        var archive = TarGz(("x/LICENSE.txt", filler), ("x/GeoLite2-City.mmdb", FakeMmdb("v2")));
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ChunkedContent(archive) });
        using var service = new GeoIpService(Config(maxBytes: 16 * 1024), null, _dir, handler);
        await ExistingDatabaseAsync(service);

        var result = await service.DownloadDatabaseAsync();

        Assert.Equal(NetUtilsErrors.DownloadTooLargeCode, result.Error?.Code);
        Assert.Equal(FakeMmdb("v1"), await File.ReadAllBytesAsync(service.DatabasePath));
        Assert.Equal([service.DatabasePath], Directory.GetFileSystemEntries(Path.GetDirectoryName(service.DatabasePath)!));
    }

    [Fact]
    public async Task A_small_archive_that_inflates_past_the_limit_is_refused()
    {
        var bomb = TarGz(("x/GeoLite2-City.mmdb", new byte[4 * 1024 * 1024]));   // zeros: a few KB compressed, 4 MB unpacked
        Assert.True(bomb.Length < 64 * 1024, $"the test archive must stay small ({bomb.Length} bytes)");
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bomb) });
        using var service = new GeoIpService(Config(maxBytes: 1024 * 1024), null, _dir, handler);
        await ExistingDatabaseAsync(service);

        var result = await service.DownloadDatabaseAsync();

        Assert.Equal(NetUtilsErrors.DownloadTooLargeCode, result.Error?.Code);
        Assert.Equal(FakeMmdb("v1"), await File.ReadAllBytesAsync(service.DatabasePath));
        Assert.Equal([service.DatabasePath], Directory.GetFileSystemEntries(Path.GetDirectoryName(service.DatabasePath)!));
    }

    [Fact]
    public async Task A_download_whose_checksum_does_not_match_installs_nothing()
    {
        var archive = TarGz(("x/GeoLite2-City.mmdb", FakeMmdb("v2")));
        var handler = new RecordingHandler(request => request.RequestUri!.ToString() == ChecksumUrl
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{new string('0', 64)}  GeoLite2-City_20261031.tar.gz\n") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
        using var service = new GeoIpService(Config(verifyChecksum: true), null, _dir, handler);
        await ExistingDatabaseAsync(service);

        var result = await service.DownloadDatabaseAsync();

        Assert.Equal(NetUtilsErrors.ChecksumMismatchCode, result.Error?.Code);
        Assert.Equal([ChecksumUrl, ArchiveUrl], handler.Requests.Select(r => r.RequestUri!.ToString()));
        Assert.All(handler.Requests, r => Assert.Equal("Basic", r.Headers.Authorization?.Scheme));
        Assert.Equal(FakeMmdb("v1"), await File.ReadAllBytesAsync(service.DatabasePath));
        Assert.Equal([service.DatabasePath], Directory.GetFileSystemEntries(Path.GetDirectoryName(service.DatabasePath)!));
    }

    [Fact]
    public async Task A_download_whose_checksum_matches_is_installed()
    {
        var archive = TarGz(("x/GeoLite2-City.mmdb", FakeMmdb("v2")));
        var handler = new RecordingHandler(request => request.RequestUri!.ToString() == ChecksumUrl
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{Sha256Hex(archive).ToUpperInvariant()}  GeoLite2-City_20261031.tar.gz\n") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
        using var service = new GeoIpService(Config(verifyChecksum: true), null, _dir, handler);
        await ExistingDatabaseAsync(service);

        var result = await service.DownloadDatabaseAsync();

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(FakeMmdb("v2"), await File.ReadAllBytesAsync(service.DatabasePath));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "")]
    [InlineData(HttpStatusCode.OK, "not a checksum")]
    public async Task A_checksum_that_cannot_be_obtained_refuses_the_download_without_fetching_the_archive(HttpStatusCode status, string body)
    {
        var archive = TarGz(("x/GeoLite2-City.mmdb", FakeMmdb("v2")));
        var handler = new RecordingHandler(request => request.RequestUri!.ToString() == ChecksumUrl
            ? new HttpResponseMessage(status) { Content = new StringContent(body) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
        using var service = new GeoIpService(Config(verifyChecksum: true), null, _dir, handler);

        var result = await service.DownloadDatabaseAsync();

        Assert.Equal(NetUtilsErrors.ChecksumUnavailableCode, result.Error?.Code);
        Assert.Equal([ChecksumUrl], handler.Requests.Select(r => r.RequestUri!.ToString()));
        Assert.False(File.Exists(service.DatabasePath));
    }

    [Fact]
    public async Task The_timeout_covers_the_body_not_only_the_headers()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StalledContent() });
        using var service = new GeoIpService(Config(timeoutSeconds: 1), null, _dir, handler);
        await ExistingDatabaseAsync(service);

        var result = await service.DownloadDatabaseAsync();

        Assert.Equal(NetUtilsErrors.DownloadTimeoutCode, result.Error?.Code);
        Assert.Equal(FakeMmdb("v1"), await File.ReadAllBytesAsync(service.DatabasePath));
    }

    [Fact]
    public async Task A_cancelled_download_reports_cancelled_and_startup_propagates_it()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StalledContent() });
        using var service = new GeoIpService(Config(), null, _dir, handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var result = await service.DownloadDatabaseAsync(cancellation.Token);
        Assert.Equal(NetUtilsErrors.CancelledCode, result.Error?.Code);

        using var second = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.InitializeAsync(second.Token));
    }

    [Fact]
    public async Task Refused_downloads_report_why()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var noCredentials = new GeoIpService(new GeoIpConfig(), null, _dir, handler);
        using var untrusted = new GeoIpService(new GeoIpConfig { AccountId = 1, LicenseKey = "k", DownloadUrl = "https://attacker.example/x" }, null, _dir, handler);

        Assert.Equal(NetUtilsErrors.DownloadRefusedCode, (await noCredentials.DownloadDatabaseAsync()).Error?.Code);
        Assert.Equal(NetUtilsErrors.DownloadRefusedCode, (await untrusted.DownloadDatabaseAsync()).Error?.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_swapped_reader_keeps_answering_a_lookup_that_snapshotted_it()
    {
        var archive = TarGz(("x/GeoLite2-City.mmdb", FakeMmdb("v2")));
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
        using var service = new GeoIpService(Config(), null, _dir, handler) { RetireGracePeriod = TimeSpan.FromSeconds(30) };
        await ExistingDatabaseAsync(service);
        await service.InitializeAsync();
        Assert.Equal("v1", service.OpenDatabaseDescription);

        // Many lookups while the database is replaced underneath them: none may fail with a disposed reader.
        var lookups = Task.Run(async () =>
        {
            var errors = new List<string?>();
            for (var i = 0; i < 2000; i++)
            {
                var result = await service.LookupIpAsync("8.8.8.8");
                if (result.ErrorMessage is not "Location not found") errors.Add(result.ErrorMessage);
            }
            return errors;
        });
        Assert.True((await service.DownloadDatabaseAsync()).IsSuccess);
        Assert.Equal("v2", service.OpenDatabaseDescription);

        Assert.Empty(await lookups);
    }
}

// ── DNSBL: allowlist sees the canonical address; cancellation is its own status ─────

public sealed class DnsblCanonicalAndCancellationTests
{
    private const string Ip = "93.184.215.7";

    [Theory]
    [InlineData("::ffff:93.184.215.7")]
    [InlineData("0x5d.0xb8.0xd7.0x07")]
    [InlineData("93.184.215.7")]
    public async Task The_allowlist_predicate_receives_the_canonical_address(string input)
    {
        string? seen = null;
        var checker = new DnsblChecker(new DnsblConfig(), null, (_, _) => throw new SocketException((int)SocketError.HostNotFound));
        var request = new DnsblCheckRequest
        {
            Ipv4Services = ["a.test"],
            IsAllowedAsync = (ip, _) => { seen = ip; return Task.FromResult(true); }
        };

        var result = await checker.CheckIpAsync(input, request);

        Assert.Equal(Ip, seen);
        Assert.Equal(DnsblCheckStatus.NotBlacklisted, result.Status);
        Assert.Equal(input, result.IpAddress);                            // the caller's spelling is echoed back
    }

    [Fact]
    public async Task A_check_cancelled_by_the_caller_is_reported_as_Cancelled_not_Error()
    {
        using var cancellation = new CancellationTokenSource();
        var checker = new DnsblChecker(new DnsblConfig(), null, async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return [];
        });
        var request = new DnsblCheckRequest { Ipv4Services = ["a.test", "b.test"], Timeout = TimeSpan.FromSeconds(30) };

        var pending = checker.CheckIpAsync(Ip, request, cancellation.Token);
        cancellation.CancelAfter(50);
        var result = await pending;

        Assert.Equal(DnsblCheckStatus.Cancelled, result.Status);
        Assert.False(result.IsBlacklisted);
        Assert.Equal(IpAddressType.IPv4, result.AddressType);
        Assert.Equal("The DNSBL check was cancelled", result.ErrorMessage);
    }

    [Fact]
    public async Task A_cancelled_allowlist_predicate_is_reported_as_Cancelled_too()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var checker = new DnsblChecker(new DnsblConfig(), null, (_, _) => Task.FromResult(Array.Empty<IPAddress>()));
        var request = new DnsblCheckRequest
        {
            Ipv4Services = ["a.test"],
            IsAllowedAsync = (_, ct) => Task.FromCanceled<bool>(ct)
        };

        var result = await checker.CheckIpAsync(Ip, request, cancellation.Token);

        Assert.Equal(DnsblCheckStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task A_per_zone_timeout_is_still_a_failed_zone_not_a_cancellation()
    {
        var checker = new DnsblChecker(new DnsblConfig(), null, async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return [];
        });
        var request = new DnsblCheckRequest { Ipv4Services = ["a.test"], Timeout = TimeSpan.FromMilliseconds(50) };

        var result = await checker.CheckIpAsync(Ip, request);

        Assert.Equal(DnsblCheckStatus.Indeterminate, result.Status);
        Assert.Equal(["a.test"], result.FailedServices);
    }
}

// ── Library: live config changes through LibraryManager ───────────────────────────

public sealed class NetUtilsConfigReloadTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cl_netutils_reload_" + Guid.NewGuid().ToString("N"));
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

    private async Task ApplyAsync(string json)
    {
        var result = await _manager!.UpdateConfigAsync("CL.NetUtils", "netutils", json);
        Assert.True(result.Validation.IsValid, string.Join("; ", result.Validation.Errors));
        Assert.Null(result.ApplyError);
    }

    private async Task<string> DatabaseAsync(string tag)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, $"{tag}.mmdb");
        await File.WriteAllBytesAsync(path, FakeMmdb(tag));
        return path;
    }

    [Fact]
    public void The_library_applies_config_changes_live() => Assert.IsAssignableFrom<IConfigReloadable>(new NetUtilsLibrary());

    [Fact]
    public async Task DNSBL_can_be_turned_on_and_off_and_its_zones_changed_without_a_restart()
    {
        var lib = await StartAsync("""{ "enabled": true, "dnsbl": { "enabled": false }, "geoIp": { "enabled": false } }""");
        Assert.False(lib.HasDnsbl);

        await ApplyAsync("""{ "enabled": true, "dnsbl": { "enabled": true, "ipv4Services": ["a.test"], "timeoutSeconds": 7 }, "geoIp": { "enabled": false } }""");
        Assert.True(lib.HasDnsbl);
        Assert.True(lib.Dnsbl.IsEnabled);
        var checker = lib.Dnsbl;

        await ApplyAsync("""{ "enabled": true, "dnsbl": { "enabled": true, "ipv4Services": ["b.test"], "timeoutSeconds": 7 }, "geoIp": { "enabled": false } }""");
        Assert.NotSame(checker, lib.Dnsbl);                                 // rebuilt with the new zones

        await ApplyAsync("""{ "enabled": true, "dnsbl": { "enabled": false }, "geoIp": { "enabled": false } }""");
        Assert.False(lib.HasDnsbl);
        Assert.False(lib.Dnsbl.IsEnabled);
        Assert.Equal(DnsblCheckStatus.Error, (await lib.Dnsbl.CheckIpAsync("93.184.215.7")).Status);
    }

    [Fact]
    public async Task GeoIP_is_rebuilt_from_new_settings_and_a_failing_one_keeps_the_previous_database()
    {
        var v1 = await DatabaseAsync("v1");
        var v2 = await DatabaseAsync("v2");
        var lib = await StartAsync($$"""{ "enabled": true, "dnsbl": { "enabled": false }, "geoIp": { "enabled": true, "databasePath": {{JsonSerializer.Serialize(v1)}} } }""");
        Assert.True(lib.HasGeoIp);
        Assert.Equal("v1", lib.GeoIp.OpenDatabaseDescription);
        var first = lib.GeoIp;

        await ApplyAsync($$"""{ "enabled": true, "dnsbl": { "enabled": false }, "geoIp": { "enabled": true, "databasePath": {{JsonSerializer.Serialize(v2)}} } }""");
        Assert.True(lib.HasGeoIp);
        Assert.NotSame(first, lib.GeoIp);
        Assert.Equal("v2", lib.GeoIp.OpenDatabaseDescription);
        Assert.Equal("v1", first.OpenDatabaseDescription);                 // the replaced service is still usable during its grace period

        var broken = Path.Combine(_root, "broken.mmdb");
        await File.WriteAllTextAsync(broken, "not a MaxMind database");
        await ApplyAsync($$"""{ "enabled": true, "dnsbl": { "enabled": false }, "geoIp": { "enabled": true, "databasePath": {{JsonSerializer.Serialize(broken)}} } }""");
        Assert.True(lib.HasGeoIp);
        Assert.Equal("v2", lib.GeoIp.OpenDatabaseDescription);             // the previous database keeps serving
        var health = await lib.HealthCheckAsync();
        Assert.Equal(HealthStatusLevel.Degraded, health.Status);
        Assert.Contains("failed", health.Message);

        await ApplyAsync("""{ "enabled": true, "dnsbl": { "enabled": false }, "geoIp": { "enabled": false } }""");
        Assert.False(lib.HasGeoIp);
        Assert.Equal(HealthStatusLevel.Healthy, (await lib.HealthCheckAsync()).Status);
    }

    [Fact]
    public async Task Turning_the_library_off_and_on_applies_live()
    {
        var lib = await StartAsync("""{ "enabled": true, "dnsbl": { "enabled": true }, "geoIp": { "enabled": false } }""");
        Assert.True(lib.HasDnsbl);

        await ApplyAsync("""{ "enabled": false, "dnsbl": { "enabled": true }, "geoIp": { "enabled": false } }""");
        Assert.False(lib.HasDnsbl);
        Assert.Equal("NetUtils library is disabled", (await lib.HealthCheckAsync()).Message);

        await ApplyAsync("""{ "enabled": true, "dnsbl": { "enabled": true }, "geoIp": { "enabled": false } }""");
        Assert.True(lib.HasDnsbl);
        Assert.Equal(HealthStatusLevel.Healthy, (await lib.HealthCheckAsync()).Status);
    }
}
