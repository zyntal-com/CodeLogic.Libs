using System.Net;
using System.Security.Cryptography;
using System.Text;
using CL.Common.Compression;
using CL.Common.Generators;
using CL.Common.Parser;
using CL.Common.Security;
using CL.Common.Strings;
using CL.Common.Time;
using CL.Common.Web;
using CodeLogic.Core.Results;
using Xunit;

namespace Common.Tests;

// Regression tests for the 2026-11 security audit fixes in CL.Common.

public class ClientIpTests
{
    private static readonly TrustedProxies Proxies =
        TrustedProxies.Parse(["10.0.0.0/8", "203.0.113.7", "2001:db8::/32"]).ValueOrThrow();

    [Fact]
    public void Untrusted_peer_is_the_client_and_its_header_is_ignored()
    {
        Assert.Equal("198.51.100.20", HttpHeaderHelper.GetEffectiveClientIp("198.51.100.20", "1.2.3.4", Proxies));
        // Old overload: a public peer can no longer spoof its address.
        Assert.Equal("198.51.100.20", HttpHeaderHelper.GetEffectiveClientIp("198.51.100.20", "1.2.3.4"));
    }

    [Fact]
    public void Trusted_peer_walks_header_right_to_left_skipping_trusted_hops()
    {
        // client spoofs "6.6.6.6", real client 198.51.100.9, then two trusted proxies.
        var xff = "6.6.6.6, 198.51.100.9, 10.1.2.3";
        Assert.Equal("198.51.100.9", HttpHeaderHelper.GetEffectiveClientIp("203.0.113.7", xff, Proxies));
    }

    [Fact]
    public void All_trusted_hops_return_the_leftmost()
    {
        Assert.Equal("10.0.0.5", HttpHeaderHelper.GetEffectiveClientIp("10.0.0.1", "10.0.0.5, 10.0.0.4", Proxies));
    }

    [Fact]
    public void Trusted_peer_without_header_is_returned()
    {
        Assert.Equal("10.0.0.1", HttpHeaderHelper.GetEffectiveClientIp("10.0.0.1", null, Proxies));
        Assert.Equal("10.0.0.1", HttpHeaderHelper.GetEffectiveClientIp("10.0.0.1", "  ", Proxies));
    }

    [Fact]
    public void Unparsable_entry_stops_the_walk_and_is_never_returned()
    {
        Assert.Equal("10.0.0.2", HttpHeaderHelper.GetEffectiveClientIp("10.0.0.1", "1.2.3.4, <script>, 10.0.0.2", Proxies));
        Assert.Equal("10.0.0.1", HttpHeaderHelper.GetEffectiveClientIp("10.0.0.1", "unknown", Proxies));
        Assert.Equal("10.0.0.1", HttpHeaderHelper.GetEffectiveClientIp("10.0.0.1", "' OR 1=1 --", Proxies));
    }

    [Fact]
    public void Ports_brackets_and_mapped_addresses_are_normalised()
    {
        Assert.Equal("198.51.100.9", HttpHeaderHelper.GetEffectiveClientIp("10.0.0.1:5000", "198.51.100.9:443", Proxies));
        Assert.Equal("2001:db9::1", HttpHeaderHelper.GetEffectiveClientIp("[2001:db8::5]:443", "[2001:db9::1]:80", Proxies));
        Assert.Equal("198.51.100.9", HttpHeaderHelper.GetEffectiveClientIp("::ffff:10.0.0.1", "::ffff:198.51.100.9", Proxies));
    }

    [Fact]
    public void Missing_or_unparsable_remote_returns_unspecified()
    {
        Assert.Equal("0.0.0.0", HttpHeaderHelper.GetEffectiveClientIp(null, "1.2.3.4"));
        Assert.Equal("0.0.0.0", HttpHeaderHelper.GetEffectiveClientIp("garbage", "1.2.3.4"));
        Assert.Null(HttpHeaderHelper.GetEffectiveClientIpAddress("garbage", null, Proxies));
    }

    [Fact]
    public void Default_overload_trusts_private_peers()
    {
        Assert.Equal("198.51.100.9", HttpHeaderHelper.GetEffectiveClientIp("127.0.0.1", "198.51.100.9"));
        Assert.Equal("198.51.100.9", HttpHeaderHelper.GetEffectiveClientIp("::1", "198.51.100.9, 192.168.1.1"));
    }

    [Fact]
    public void None_never_reads_the_header()
    {
        Assert.Equal("127.0.0.1", HttpHeaderHelper.GetEffectiveClientIp("127.0.0.1", "198.51.100.9", TrustedProxies.None));
    }

    [Theory]
    [InlineData("1.2.3.4", "1.2.3.4")]
    [InlineData(" 1.2.3.4 ", "1.2.3.4")]
    [InlineData("1.2.3.4:8080", "1.2.3.4")]
    [InlineData("[::1]", "::1")]
    [InlineData("[::1]:443", "::1")]
    [InlineData("::ffff:192.168.0.1", "192.168.0.1")]
    [InlineData("fe80::1%3", "fe80::1")]
    [InlineData("\"1.2.3.4\"", "1.2.3.4")]
    public void TryParseIp_accepts(string input, string expected)
    {
        Assert.True(HttpHeaderHelper.TryParseIp(input, out var ip));
        Assert.Equal(expected, ip.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("127.1")]
    [InlineData("1")]
    [InlineData("1.2.3.4:99999")]
    [InlineData("1.2.3.4:")]
    [InlineData("[1.2.3.4]")]
    [InlineData("[::1")]
    [InlineData("[::1]x")]
    [InlineData("1.2.3.256")]
    [InlineData("_hidden")]
    public void TryParseIp_rejects(string? input)
    {
        Assert.False(HttpHeaderHelper.TryParseIp(input, out _));
    }

    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.1.1", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.255.0.1", true)]
    [InlineData("0.1.2.3", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.127.255.255", true)]
    [InlineData("100.128.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("::1", true)]
    [InlineData("::", true)]
    [InlineData("fe80::1", true)]
    [InlineData("febf::1", true)]
    [InlineData("fec0::1", false)]
    [InlineData("fc00::1", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("::ffff:10.0.0.1", true)]
    [InlineData("::ffff:8.8.8.8", false)]
    [InlineData("2001:4860:4860::8888", false)]
    [InlineData("not-an-ip", false)]
    public void IsPrivateIp_covers_non_public_ranges(string ip, bool expected)
    {
        Assert.Equal(expected, HttpHeaderHelper.IsPrivateIp(ip));
    }

    [Fact]
    public void Parse_rejects_invalid_entries()
    {
        var r = TrustedProxies.Parse(["10.0.0.0/8", "nope"]);
        Assert.True(r.IsFailure);
        Assert.Equal(TrustedProxies.InvalidEntryCode, r.Error!.Code);
        Assert.True(TrustedProxies.Parse(["10.0.0.0/33"]).IsFailure);
    }

    [Fact]
    public void Trusted_sets_from_addresses_networks_and_union()
    {
        var a = TrustedProxies.FromAddresses([IPAddress.Parse("203.0.113.7")]);
        var b = TrustedProxies.FromNetworks([IPNetwork.Parse("::ffff:10.0.0.0/104")]);
        var both = a.Union(b);
        Assert.True(both.IsTrusted("203.0.113.7"));
        Assert.True(both.IsTrusted("10.9.9.9"));
        Assert.False(both.IsTrusted("203.0.113.8"));
        Assert.False(both.IsTrusted("garbage"));
    }
}

public class DecompressionLimitTests
{
    private static readonly byte[] Big = new byte[1_000_000]; // zeros compress to almost nothing

    public static TheoryData<string> Algorithms => ["gzip", "brotli", "zstd", "lz4"];

    private static (Result<byte[]> Compressed, Func<byte[], long, Result<byte[]>> Decompress) Pick(string algo) => algo switch
    {
        "gzip" => (CompressionHelper.CompressGzip(Big), CompressionHelper.DecompressGzip),
        "brotli" => (CompressionHelper.CompressBrotli(Big), CompressionHelper.DecompressBrotli),
        "zstd" => (CompressionHelper.CompressZstandard(Big), CompressionHelper.DecompressZstandard),
        _ => (CompressionHelper.CompressLz4(Big), CompressionHelper.DecompressLz4),
    };

    [Theory]
    [MemberData(nameof(Algorithms))]
    public void Output_over_the_cap_fails(string algo)
    {
        var (compressed, decompress) = Pick(algo);
        var r = decompress(compressed.ValueOrThrow(), 64 * 1024);
        Assert.True(r.IsFailure);
        Assert.Equal(CompressionHelper.OutputLimitExceededCode, r.Error!.Code);
    }

    [Theory]
    [MemberData(nameof(Algorithms))]
    public void Output_at_the_cap_succeeds(string algo)
    {
        var (compressed, decompress) = Pick(algo);
        Assert.Equal(Big.Length, decompress(compressed.ValueOrThrow(), Big.Length).ValueOrThrow().Length);
    }

    [Fact]
    public void DecompressString_honours_the_cap()
    {
        var s = CompressionHelper.CompressString(new string('a', 10_000)).ValueOrThrow();
        Assert.True(CompressionHelper.DecompressString(s, 100).IsFailure);
        Assert.Equal(10_000, CompressionHelper.DecompressString(s).ValueOrThrow().Length);
    }

    [Fact]
    public void Default_cap_is_100_MiB()
    {
        Assert.Equal(100L * 1024 * 1024, CompressionHelper.DefaultMaxDecompressedBytes);
    }
}

public class PasswordHashFormatTests
{
    [Fact]
    public void New_hashes_are_self_describing_with_600k_iterations()
    {
        var hash = Hashing.HashPassword("pw");
        Assert.StartsWith("$pbkdf2-sha256$v1$600000$", hash);
        Assert.True(Hashing.VerifyPassword("pw", hash));
        Assert.False(Hashing.NeedsRehash(hash));
    }

    [Fact]
    public void Stored_iteration_count_is_used_regardless_of_the_parameter()
    {
        var hash = Hashing.HashPassword("pw", iterations: 1000);
        Assert.True(Hashing.VerifyPassword("pw", hash));
        Assert.True(Hashing.VerifyPassword("pw", hash, iterations: 5));
        Assert.True(Hashing.NeedsRehash(hash));
        Assert.False(Hashing.NeedsRehash(hash, iterations: 1000));
    }

    private static string LegacyHash(string password, int iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);
        return Convert.ToBase64String([.. salt, .. hash]);
    }

    [Fact]
    public void Legacy_hashes_still_verify_and_need_rehash()
    {
        var legacy = LegacyHash("old", 100_000);
        Assert.True(Hashing.VerifyPassword("old", legacy));
        Assert.False(Hashing.VerifyPassword("wrong", legacy));
        Assert.True(Hashing.NeedsRehash(legacy));

        var custom = LegacyHash("old", 2000);
        Assert.True(Hashing.VerifyPassword("old", custom, iterations: 2000));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("$pbkdf2-sha256$v1$abc$AAAA$AAAA")]
    [InlineData("$pbkdf2-sha256$v1$0$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("$pbkdf2-sha256$v1$999999999$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("$pbkdf2-sha256$v2$1000$AAAA$AAAA")]
    [InlineData("AAAA")]
    public void Malformed_hashes_fail_verification_and_need_rehash(string stored)
    {
        Assert.False(Hashing.VerifyPassword("pw", stored));
        Assert.True(Hashing.NeedsRehash(stored));
    }
}

public class GeneratorUniformityTests
{
    [Fact]
    public void Pin_uses_only_digits_and_every_digit_appears()
    {
        var pin = PasswordGenerator.GeneratePin(5000);
        Assert.All(pin, c => Assert.InRange(c, '0', '9'));
        Assert.Equal(10, pin.Distinct().Count());
    }

    [Fact]
    public void Strong_password_has_every_category()
    {
        for (var i = 0; i < 50; i++)
        {
            var pw = PasswordGenerator.GenerateStrong(12);
            Assert.Equal(12, pw.Length);
            Assert.Contains(pw, char.IsLower);
            Assert.Contains(pw, char.IsUpper);
            Assert.Contains(pw, char.IsDigit);
            Assert.Contains(pw, c => !char.IsLetterOrDigit(c));
        }
    }

    [Fact]
    public void Id_alphabets_are_respected()
    {
        Assert.Matches("^[A-Za-z0-9]{500}$", IdGenerator.Random(500));
        Assert.Matches("^[A-Za-z0-9_-]{500}$", IdGenerator.NanoId(500));
        Assert.Matches("^[A-Za-z0-9_-]{500}$", IdGenerator.UrlSafe(500));
        Assert.Throws<ArgumentOutOfRangeException>(() => IdGenerator.Random(0));
    }

    [Fact]
    public void Passphrase_has_requested_word_count()
    {
        Assert.Equal(6, PasswordGenerator.GeneratePassphrase(6).Split('-').Length);
    }
}

public class CronDayFieldTests
{
    private static readonly DateTime From = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc); // a Sunday

    [Fact]
    public void Both_day_fields_restricted_are_ored()
    {
        // "midnight on the 15th OR on Mondays": the next is Monday 2026-11-02, not a Monday the 15th.
        var next = CronParser.GetNextOccurrence("0 0 15 * 1", From).ValueOrThrow();
        Assert.Equal(new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc), next);

        var after = CronParser.GetNextOccurrence("0 0 15 * 1", new DateTime(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc)).ValueOrThrow();
        Assert.Equal(new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc), after); // Sunday the 15th
    }

    [Fact]
    public void Only_one_day_field_restricted_uses_it_alone()
    {
        Assert.Equal(new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc),
            CronParser.GetNextOccurrence("0 0 15 * *", From).ValueOrThrow());
        Assert.Equal(new DateTime(2026, 11, 4, 0, 0, 0, DateTimeKind.Utc),
            CronParser.GetNextOccurrence("0 0 * * 3", From).ValueOrThrow());
        // A step starting with '*' counts as unrestricted, as in Vixie cron, so the fields are ANDed:
        // an odd day of the month that is also a Monday (Nov 2 is even; Nov 9 is the first odd Monday).
        Assert.Equal(new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc),
            CronParser.GetNextOccurrence("0 0 */2 * 1", From).ValueOrThrow());
    }

    [Fact]
    public void Time_zone_overload_also_ors()
    {
        var next = CronParser.GetNextOccurrence("0 0 15 * 1", new DateTimeOffset(From), null).ValueOrThrow();
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero), next);
    }
}

public class MiscFixTests
{
    [Theory]
    [InlineData("abcdefgh", 2, "...", "ab")]
    [InlineData("abcdefgh", 3, "...", "abc")]
    [InlineData("abcdefgh", 4, "...", "a...")]
    [InlineData("abcdefgh", 0, "...", "")]
    [InlineData("abcdefgh", -1, "...", "")]
    [InlineData("abcdefgh", 4, "", "abcd")]
    public void Truncate_never_throws_and_never_exceeds_max(string value, int max, string suffix, string expected)
    {
        Assert.Equal(expected, StringHelper.Truncate(value, max, suffix));
    }

    [Fact]
    public void Truncate_tolerates_null_suffix()
    {
        Assert.Equal("abcd", StringHelper.Truncate("abcdefgh", 4, null!));
    }

    [Theory]
    [InlineData(2026, 10, 30, 2026, 11, 2)] // Friday -> Monday
    [InlineData(2026, 10, 31, 2026, 11, 2)] // Saturday -> Monday
    [InlineData(2026, 11, 1, 2026, 11, 2)]  // Sunday -> Monday
    [InlineData(2026, 11, 2, 2026, 11, 3)]  // Monday -> Tuesday (strictly after)
    public void NextBusinessDay_is_strictly_after(int y, int m, int d, int ey, int em, int ed)
    {
        Assert.Equal(new DateTime(ey, em, ed, 9, 30, 0), DateTimeHelper.NextBusinessDay(new DateTime(y, m, d, 9, 30, 0)));
    }

    [Fact]
    public void MatchesPattern_times_out_on_catastrophic_backtracking()
    {
        var evil = new string('a', 40) + "!";
        Assert.False(StringValidator.MatchesPattern(evil, "^(a+)+$", TimeSpan.FromMilliseconds(50)));
        Assert.True(StringValidator.MatchesPattern("abc", "^a.c$"));
    }

    private sealed class FixedHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }

    [Fact]
    public async Task Failed_response_body_is_truncated_in_the_error()
    {
        var body = new string('x', 10_000);
        using var client = new HttpClient(new FixedHandler(HttpStatusCode.InternalServerError, body));
        var r = await HttpClientHelper.GetStringAsync(client, "http://example.test/");
        Assert.True(r.IsFailure);
        Assert.StartsWith("HTTP 500: ", r.Error!.Message);
        Assert.True(r.Error.Message.Length < 400, $"message length {r.Error.Message.Length}");
        Assert.Contains("(10000 chars)", r.Error.Message);
    }

    [Fact]
    public async Task Short_failed_response_body_is_kept()
    {
        using var client = new HttpClient(new FixedHandler(HttpStatusCode.BadRequest, "bad input"));
        var r = await HttpClientHelper.GetStringAsync(client, "http://example.test/");
        Assert.Equal("HTTP 400: bad input", r.Error!.Message);
    }
}
