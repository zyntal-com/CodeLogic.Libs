using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using CL.NetUtils.Models;
using CL.NetUtils.Services;
using Xunit;

namespace NetUtils.Tests;

// ── DNSBL: ParallelQueries and failed-zone reporting (fake resolver, offline) ──────

public sealed class DnsblCheckerResolverTests
{
    // A public address: documentation ranges such as 203.0.113.0/24 are skipped without any query.
    private const string Ip = "93.184.215.7";
    private const string Reversed = "7.215.184.93";

    private static readonly IPAddress Listed = IPAddress.Parse("127.0.0.2");
    private static readonly IPAddress SpamhausError = IPAddress.Parse("127.255.255.254");

    private static SocketException NxDomain() => new((int)SocketError.HostNotFound);

    /// <summary>A resolver that answers per zone and records how many lookups run at once.</summary>
    private sealed class FakeResolver(Func<string, Task<IPAddress[]>> answer)
    {
        private int _active;
        public int MaxConcurrent;
        public List<string> Lookups { get; } = [];

        public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
        {
            lock (Lookups) Lookups.Add(host);
            var now = Interlocked.Increment(ref _active);
            InterlockedMax(ref MaxConcurrent, now);
            try
            {
                await Task.Delay(50, ct);
                return await answer(host[(Reversed.Length + 1)..]);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int seen;
            while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
        }
    }

    private static DnsblCheckRequest Request(bool parallel, string[] primaries, string[]? fallbacks = null, TimeSpan? timeout = null) => new()
    {
        Ipv4Services = primaries,
        Ipv4FallbackServices = fallbacks ?? [],
        ParallelQueries = parallel,
        Timeout = timeout ?? TimeSpan.FromSeconds(30)
    };

    private static DnsblChecker Checker(FakeResolver resolver) => new(new DnsblConfig(), null, resolver.ResolveAsync);

    [Fact]
    public async Task ParallelQueries_true_queries_zones_concurrently()
    {
        var resolver = new FakeResolver(_ => Task.FromException<IPAddress[]>(NxDomain()));
        var result = await Checker(resolver).CheckIpAsync(Ip, Request(true, ["a.test", "b.test", "c.test"]));

        Assert.Equal(DnsblCheckStatus.NotBlacklisted, result.Status);
        Assert.Equal(3, resolver.Lookups.Count);
        Assert.True(resolver.MaxConcurrent > 1, $"max concurrent lookups {resolver.MaxConcurrent}");
    }

    [Fact]
    public async Task ParallelQueries_false_queries_one_at_a_time_and_stops_at_the_first_listing()
    {
        var resolver = new FakeResolver(zone => zone == "b.test"
            ? Task.FromResult(new[] { Listed })
            : Task.FromException<IPAddress[]>(NxDomain()));
        var result = await Checker(resolver).CheckIpAsync(Ip, Request(false, ["a.test", "b.test", "c.test"]));

        Assert.Equal(DnsblCheckStatus.Blacklisted, result.Status);
        Assert.Equal("b.test", result.MatchedService);
        Assert.Equal(1, resolver.MaxConcurrent);
        Assert.DoesNotContain(resolver.Lookups, l => l.EndsWith("c.test"));
    }

    [Theory]
    [InlineData("93.184.215.7")]
    [InlineData("93.184.55047")]                // shorthand: last part fills two bytes (215 * 256 + 7)
    [InlineData("0x5d.0xb8.0xd7.0x07")]         // hex octets
    [InlineData("::ffff:93.184.215.7")]         // IPv4-mapped IPv6 uses the IPv4 zones
    public async Task Query_name_comes_from_the_address_bytes_not_the_input_text(string input)
    {
        var resolver = new FakeResolver(_ => Task.FromException<IPAddress[]>(NxDomain()));

        var result = await Checker(resolver).CheckIpAsync(input, Request(false, ["a.test"]));

        Assert.Equal(DnsblCheckStatus.NotBlacklisted, result.Status);
        Assert.Equal([Reversed + ".a.test"], resolver.Lookups);
    }

    [Fact]
    public void Config_overload_passes_ParallelQueries_through()
    {
        // The configured flag reaches the request (default true); false must not be ignored.
        Assert.True(new DnsblConfig().ParallelQueries);
        Assert.True(new DnsblCheckRequest().ParallelQueries);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Config_ParallelQueries_controls_concurrency(bool parallel)
    {
        var resolver = new FakeResolver(_ => Task.FromException<IPAddress[]>(NxDomain()));
        var config = new DnsblConfig { Ipv4Services = ["a.test", "b.test", "c.test"], Ipv4FallbackServices = [], ParallelQueries = parallel };
        var result = await new DnsblChecker(config, null, resolver.ResolveAsync).CheckIpAsync(Ip);

        Assert.Equal(DnsblCheckStatus.NotBlacklisted, result.Status);
        Assert.Equal(parallel, resolver.MaxConcurrent > 1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Resolver_failure_is_indeterminate_not_clean(bool parallel)
    {
        var resolver = new FakeResolver(zone => zone == "broken.test"
            ? Task.FromException<IPAddress[]>(new SocketException((int)SocketError.TryAgain))
            : Task.FromException<IPAddress[]>(NxDomain()));
        var result = await Checker(resolver).CheckIpAsync(Ip, Request(parallel, ["ok.test", "broken.test"]));

        Assert.Equal(DnsblCheckStatus.Indeterminate, result.Status);
        Assert.False(result.IsBlacklisted);
        Assert.Equal(["broken.test"], result.FailedServices);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task Timeout_is_indeterminate()
    {
        var resolver = new FakeResolver(async _ => { await Task.Delay(Timeout.Infinite); return []; });
        var result = await Checker(resolver).CheckIpAsync(Ip, Request(true, ["slow.test"], timeout: TimeSpan.FromMilliseconds(100)));

        Assert.Equal(DnsblCheckStatus.Indeterminate, result.Status);
        Assert.Equal(["slow.test"], result.FailedServices);
    }

    [Fact]
    public async Task Dnsbl_error_answer_is_indeterminate()
    {
        var resolver = new FakeResolver(_ => Task.FromResult(new[] { SpamhausError }));
        var result = await Checker(resolver).CheckIpAsync(Ip, Request(true, ["zen.test"]));

        Assert.Equal(DnsblCheckStatus.Indeterminate, result.Status);
        Assert.Equal(["zen.test"], result.FailedServices);
    }

    [Fact]
    public async Task Nxdomain_everywhere_is_clean()
    {
        var resolver = new FakeResolver(_ => Task.FromException<IPAddress[]>(NxDomain()));
        var result = await Checker(resolver).CheckIpAsync(Ip, Request(true, ["a.test"], ["fb.test"]));

        Assert.Equal(DnsblCheckStatus.NotBlacklisted, result.Status);
        Assert.Empty(result.FailedServices);
        Assert.Null(result.ErrorMessage);
        Assert.Equal(2, resolver.Lookups.Count);   // fallback consulted because no primary listed
    }

    [Fact]
    public async Task Listing_wins_over_failed_zones_and_fallbacks_are_skipped()
    {
        var resolver = new FakeResolver(zone => zone switch
        {
            "listed.test" => Task.FromResult(new[] { Listed }),
            _ => Task.FromException<IPAddress[]>(new SocketException((int)SocketError.TryAgain)),
        });
        var result = await Checker(resolver).CheckIpAsync(Ip, Request(true, ["broken.test", "listed.test"], ["fb.test"]));

        Assert.Equal(DnsblCheckStatus.Blacklisted, result.Status);
        Assert.Equal("listed.test", result.MatchedService);
        Assert.Equal(["broken.test"], result.FailedServices);
        Assert.DoesNotContain(resolver.Lookups, l => l.EndsWith("fb.test"));
    }
}

// ── GeoIP: trusted download URL and selective, atomic install (offline) ────────────

public sealed class GeoIpDownloadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cl_geoip_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static readonly byte[] MmdbMarker = [0xAB, 0xCD, 0xEF, .."MaxMind.com"u8];

    /// <summary>A fake database: payload followed by the MaxMind metadata marker.</summary>
    /// <summary>
    /// A real (minimal) MaxMind DB that the MaxMind reader opens: a one-node search tree whose records mean "not found",
    /// the data-section separator, and the metadata map. <paramref name="tag"/> is the English description, so tests can
    /// tell databases apart.
    /// </summary>
    private static byte[] FakeMmdb(string tag, string databaseType = "GeoLite2-City")
    {
        List<byte> b = [0, 0, 1, 0, 0, 1];                              // node_count 1, record_size 24: both records = "no data"
        b.AddRange(new byte[16]);                                       // data section separator (empty data section)
        b.AddRange(MmdbMarker);
        void Str(string s)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(s);
            if (bytes.Length < 29) b.Add((byte)(0x40 | bytes.Length));
            else { b.Add(0x40 | 29); b.Add((byte)(bytes.Length - 29)); }
            b.AddRange(bytes);
        }
        void Unsigned(byte type, ulong value, bool extended = false)
        {
            var bytes = BitConverter.GetBytes(value).Reverse().SkipWhile(x => x == 0).ToArray();
            if (extended) { b.Add((byte)bytes.Length); b.Add((byte)(type - 7)); }
            else b.Add((byte)((type << 5) | bytes.Length));
            b.AddRange(bytes);
        }
        void U16(ulong v) => Unsigned(5, v);
        b.Add(0xE0 | 9);                                                // metadata: map of 9 entries
        Str("binary_format_major_version"); U16(2);
        Str("binary_format_minor_version"); U16(0);
        Str("build_epoch"); Unsigned(9, 1_793_000_000, extended: true);  // uint64
        Str("database_type"); Str(databaseType);
        Str("description"); b.Add(0xE0 | 1); Str("en"); Str(tag);
        Str("ip_version"); U16(6);
        Str("languages"); b.Add(1); b.Add(11 - 7); Str("en");            // array of 1
        Str("node_count"); Unsigned(6, 1);                               // uint32
        Str("record_size"); U16(24);
        return [.. b];
    }

    /// <summary>A corrupt file that still contains the metadata marker (the re-audit's reproduction).</summary>
    private static byte[] MarkerOnlyCorrupt() => [.."fake-db:v2"u8, ..MmdbMarker, 0xFF];

    private static byte[] TarGz(params (string Name, byte[] Data)[] files)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (name, data) in files)
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(data) });
        }
        return output.ToArray();
    }

    private static GeoIpConfig Config(string? url = null, List<string>? hosts = null) => new()
    {
        AccountId = 42,
        LicenseKey = "secret-key",
        DownloadUrl = url ?? "https://download.maxmind.com/geoip/databases/GeoLite2-City/download?suffix=tar.gz",
        TrustedDownloadHosts = hosts ?? ["download.maxmind.com"]
    };

    [Theory]
    [InlineData("http://download.maxmind.com/geoip/databases/GeoLite2-City/download?suffix=tar.gz")]
    [InlineData("https://attacker.example/steal")]
    [InlineData("https://download.maxmind.com.attacker.example/x")]
    [InlineData("https://user:pw@download.maxmind.com/x")]
    [InlineData("not a url")]
    public async Task Untrusted_download_url_sends_no_request_and_no_credentials(string url)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var service = new GeoIpService(Config(url), null, _dir, handler);

        await service.DownloadDatabaseAsync();

        Assert.Empty(handler.Requests);
        Assert.False(Config(url).TryGetTrustedDownloadUri(out _, out var error));
        Assert.NotEmpty(error);
        Assert.False(new NetUtilsConfig { GeoIp = Config(url) }.Validate().IsValid);
    }

    [Fact]
    public void Default_config_is_trusted_and_valid()
    {
        Assert.True(new GeoIpConfig().TryGetTrustedDownloadUri(out var uri, out _));
        Assert.Equal("download.maxmind.com", uri!.Host);
        Assert.True(new NetUtilsConfig().Validate().IsValid);
    }

    [Fact]
    public void Explicitly_trusted_mirror_is_accepted()
    {
        Assert.True(Config("https://mirror.internal/geo.tar.gz", ["download.maxmind.com", "mirror.internal"])
            .TryGetTrustedDownloadUri(out _, out _));
    }

    [Fact]
    public async Task Download_installs_only_the_mmdb_with_credentials_on_the_trusted_host()
    {
        var archive = TarGz(
            ("GeoLite2-City_20261031/LICENSE.txt", "license"u8.ToArray()),
            ("GeoLite2-City_20261031/README.txt", "readme"u8.ToArray()),
            ("GeoLite2-City_20261031/../../escape.mmdb.txt", "nope"u8.ToArray()),
            ("GeoLite2-City_20261031/GeoLite2-City.mmdb", FakeMmdb("v2")));
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
        using var service = new GeoIpService(Config(), null, _dir, handler);
        Directory.CreateDirectory(Path.GetDirectoryName(service.DatabasePath)!);
        await File.WriteAllBytesAsync(service.DatabasePath, FakeMmdb("v1"));

        await service.DownloadDatabaseAsync();

        var request = Assert.Single(handler.Requests);
        Assert.Equal("download.maxmind.com", request.RequestUri!.Host);
        Assert.Equal("Basic", request.Headers.Authorization!.Scheme);

        Assert.Equal(FakeMmdb("v2"), await File.ReadAllBytesAsync(service.DatabasePath));
        // Only the database is left: no licence/readme, no temp directory, nothing outside.
        var dbDir = Path.GetDirectoryName(service.DatabasePath)!;
        Assert.Equal([service.DatabasePath], Directory.GetFileSystemEntries(dbDir));
        Assert.Equal([Path.Combine(_dir, "geoip")], Directory.GetFileSystemEntries(_dir));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AutoUpdate_refreshes_an_existing_database_on_startup_only_when_enabled(bool autoUpdate)
    {
        var archive = TarGz(("GeoLite2-City_20261031/GeoLite2-City.mmdb", FakeMmdb("v2")));
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
        var config = Config();
        config.AutoUpdate = autoUpdate;
        using var service = new GeoIpService(config, null, _dir, handler);
        Directory.CreateDirectory(Path.GetDirectoryName(service.DatabasePath)!);
        await File.WriteAllBytesAsync(service.DatabasePath, FakeMmdb("v1"));

        await service.EnsureDatabaseAsync();

        Assert.Equal(autoUpdate ? 1 : 0, handler.Requests.Count);
        Assert.Equal(FakeMmdb(autoUpdate ? "v2" : "v1"), await File.ReadAllBytesAsync(service.DatabasePath));
    }

    [Fact]
    public async Task A_failed_auto_update_keeps_the_existing_database()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var config = Config();
        config.AutoUpdate = true;
        using var service = new GeoIpService(config, null, _dir, handler);
        Directory.CreateDirectory(Path.GetDirectoryName(service.DatabasePath)!);
        await File.WriteAllBytesAsync(service.DatabasePath, FakeMmdb("v1"));

        await service.EnsureDatabaseAsync();                               // no exception: startup continues

        Assert.Single(handler.Requests);
        Assert.Equal(FakeMmdb("v1"), await File.ReadAllBytesAsync(service.DatabasePath));
    }

    [Fact]
    public void The_test_database_builder_produces_files_the_maxmind_reader_opens()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "probe.mmdb");
        File.WriteAllBytes(path, FakeMmdb("probe"));
        using var reader = new MaxMind.GeoIP2.DatabaseReader(path);
        Assert.Equal("GeoLite2-City", reader.Metadata.DatabaseType);
        Assert.False(reader.TryCity(IPAddress.Parse("8.8.8.8"), out _));
    }

    [Theory]
    [InlineData("marker")]
    [InlineData("country")]
    public async Task Corrupt_or_wrong_type_downloads_never_replace_a_working_database(string kind)
    {
        var payload = kind == "marker" ? MarkerOnlyCorrupt() : FakeMmdb("country", "GeoLite2-Country");
        var archive = TarGz(("GeoLite2-City_20261031/GeoLite2-City.mmdb", payload));
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
        using var service = new GeoIpService(Config(), null, _dir, handler);
        Directory.CreateDirectory(Path.GetDirectoryName(service.DatabasePath)!);
        await File.WriteAllBytesAsync(service.DatabasePath, FakeMmdb("v1"));
        await service.InitializeAsync();                                 // a working, open database
        Assert.Equal("v1", service.OpenDatabaseDescription);

        await service.DownloadDatabaseAsync();

        Assert.Equal(FakeMmdb("v1"), await File.ReadAllBytesAsync(service.DatabasePath));
        Assert.Equal("v1", service.OpenDatabaseDescription);             // the reader was never closed
        Assert.Equal("Invalid IP address format", (await service.LookupIpAsync("nope")).ErrorMessage);
        Assert.NotEqual("GeoIP service not initialized", (await service.LookupIpAsync("8.8.8.8")).ErrorMessage);   // still serving
    }

    [Fact]
    public async Task A_valid_download_switches_an_open_reader_without_a_gap()
    {
        var archive = TarGz(("GeoLite2-City_20261031/GeoLite2-City.mmdb", FakeMmdb("v2")));
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
        using var service = new GeoIpService(Config(), null, _dir, handler);
        Directory.CreateDirectory(Path.GetDirectoryName(service.DatabasePath)!);
        await File.WriteAllBytesAsync(service.DatabasePath, FakeMmdb("v1"));
        await service.InitializeAsync();

        await service.DownloadDatabaseAsync();                           // replaces the file the open reader came from

        Assert.Equal(FakeMmdb("v2"), await File.ReadAllBytesAsync(service.DatabasePath));
        Assert.Equal("v2", service.OpenDatabaseDescription);
    }

    [Fact]
    public async Task Invalid_archive_leaves_the_existing_database_untouched()
    {
        var archive = TarGz(("x/GeoLite2-City.mmdb", "not a maxmind database"u8.ToArray()));
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
        using var service = new GeoIpService(Config(), null, _dir, handler);
        Directory.CreateDirectory(Path.GetDirectoryName(service.DatabasePath)!);
        await File.WriteAllBytesAsync(service.DatabasePath, FakeMmdb("v1"));

        await service.DownloadDatabaseAsync();

        Assert.Equal(FakeMmdb("v1"), await File.ReadAllBytesAsync(service.DatabasePath));
        Assert.Equal([service.DatabasePath], Directory.GetFileSystemEntries(Path.GetDirectoryName(service.DatabasePath)!));
    }

    [Fact]
    public void Zip_archives_are_supported_and_prefer_the_configured_database_name()
    {
        Directory.CreateDirectory(_dir);
        var zipPath = Path.Combine(_dir, "db.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var (name, data) in new[] { ("a/Other.mmdb", FakeMmdb("other")), ("a/GeoLite2-City.mmdb", FakeMmdb("city")) })
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(data);
            }
        }

        var output = Path.Combine(_dir, "out.mmdb");
        GeoIpService.ExtractDatabase(zipPath, output, "GeoLite2-City.mmdb");

        Assert.Equal(FakeMmdb("city"), File.ReadAllBytes(output));
    }

    [Fact]
    public async Task Archive_without_an_mmdb_installs_nothing()
    {
        var archive = TarGz(("x/LICENSE.txt", "license"u8.ToArray()));
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
        using var service = new GeoIpService(Config(), null, _dir, handler);

        await service.DownloadDatabaseAsync();

        Assert.False(File.Exists(service.DatabasePath));
        Assert.Empty(Directory.GetFileSystemEntries(Path.GetDirectoryName(service.DatabasePath)!));
    }
}
