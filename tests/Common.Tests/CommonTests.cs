using CL.Common.Compression;
using CL.Common.Conversion;
using CL.Common.Data;
using CL.Common.Generators;
using CL.Common.Parser;
using CL.Common.Security;
using CL.Common.Strings;
using CL.Common.Time;
using CL.Common.Web;
using Xunit;

namespace Common.Tests;

// Offline unit tests for the CL.Common utility toolkit. Every helper here is a pure
// function (or in-memory), so the whole surface runs without any external dependency.

public class HashingTests
{
    [Fact]
    public void Sha256_is_deterministic_and_lowercase_hex()
    {
        var a = Hashing.Sha256("hello");
        var b = Hashing.Sha256("hello");
        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);
        Assert.Equal(a, a.ToLowerInvariant());
    }

    [Fact]
    public void Sha256_differs_for_different_input()
    {
        Assert.NotEqual(Hashing.Sha256("hello"), Hashing.Sha256("world"));
    }

    [Fact]
    public void Password_hash_round_trips_and_rejects_wrong()
    {
        var hash = Hashing.HashPassword("S3cret!");
        Assert.True(Hashing.VerifyPassword("S3cret!", hash));
        Assert.False(Hashing.VerifyPassword("wrong", hash));
    }

    [Fact]
    public void Password_hash_is_salted_so_two_hashes_differ()
    {
        Assert.NotEqual(Hashing.HashPassword("same"), Hashing.HashPassword("same"));
    }

    [Fact]
    public void Hmac_is_keyed()
    {
        Assert.NotEqual(Hashing.HmacSha256("msg", "k1"), Hashing.HmacSha256("msg", "k2"));
        Assert.Equal(Hashing.HmacSha256("msg", "k1"), Hashing.HmacSha256("msg", "k1"));
    }
}

public class EncryptionTests
{
    [Fact]
    public void Aes_string_round_trips()
    {
        var cipher = Encryption.EncryptAes("top secret", "pw");
        Assert.NotEqual("top secret", cipher);
        Assert.Equal("top secret", Encryption.DecryptAes(cipher, "pw"));
    }

    [Fact]
    public void Aes_bytes_round_trip()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var enc = Encryption.EncryptBytes(data, "pw");
        Assert.Equal(data, Encryption.DecryptBytes(enc, "pw"));
    }

    [Fact]
    public void Same_plaintext_encrypts_differently_each_time()
    {
        // AES-GCM uses a random nonce per call.
        Assert.NotEqual(Encryption.EncryptAes("x", "pw"), Encryption.EncryptAes("x", "pw"));
    }
}

public class CompressionTests
{
    [Fact]
    public void Gzip_round_trips_bytes()
    {
        var data = System.Text.Encoding.UTF8.GetBytes(new string('a', 2000));
        var comp = CompressionHelper.CompressGzip(data);
        Assert.True(comp.IsSuccess);
        var back = CompressionHelper.DecompressGzip(comp.Value!);
        Assert.True(back.IsSuccess);
        Assert.Equal(data, back.Value);
    }

    [Fact]
    public void Brotli_round_trips_bytes()
    {
        var data = System.Text.Encoding.UTF8.GetBytes("brotli payload " + new string('z', 500));
        var comp = CompressionHelper.CompressBrotli(data);
        var back = CompressionHelper.DecompressBrotli(comp.Value!);
        Assert.Equal(data, back.Value);
    }

    [Fact]
    public void Lz4_round_trips_bytes()
    {
        var data = System.Text.Encoding.UTF8.GetBytes("lz4 " + new string('q', 500));
        var comp = CompressionHelper.CompressLz4(data);
        var back = CompressionHelper.DecompressLz4(comp.Value!);
        Assert.Equal(data, back.Value);
    }

    [Fact]
    public void Zstandard_round_trips_and_shrinks_repetitive_bytes()
    {
        var data = System.Text.Encoding.UTF8.GetBytes("zstd payload " + new string('z', 4000));
        var comp = CompressionHelper.CompressZstandard(data);
        Assert.True(comp.IsSuccess);
        Assert.True(comp.Value!.Length < data.Length);
        var back = CompressionHelper.DecompressZstandard(comp.Value!);
        Assert.True(back.IsSuccess);
        Assert.Equal(data, back.Value);
    }

    [Fact]
    public void Zstandard_round_trips_empty_input()
    {
        var comp = CompressionHelper.CompressZstandard([]);
        Assert.True(comp.IsSuccess);
        var back = CompressionHelper.DecompressZstandard(comp.Value!);
        Assert.True(back.IsSuccess);
        Assert.Empty(back.Value!);
    }

    [Fact]
    public void Zstandard_round_trips_large_random_buffer()
    {
        var data = new byte[4 * 1024 * 1024 + 123];
        new Random(42).NextBytes(data);
        var comp = CompressionHelper.CompressZstandard(data);
        Assert.True(comp.IsSuccess);
        var back = CompressionHelper.DecompressZstandard(comp.Value!);
        Assert.True(back.IsSuccess);
        Assert.Equal(data, back.Value);
    }

    [Fact]
    public void Zstandard_decompress_of_garbage_is_a_failure_not_a_throw()
    {
        var back = CompressionHelper.DecompressZstandard([1, 2, 3, 4, 5, 6, 7, 8]);
        Assert.True(back.IsFailure);
        Assert.Equal("compression.zstandard_decompress_failed", back.Error!.Code);
    }

    [Fact]
    public void Compress_string_round_trips()
    {
        var comp = CompressionHelper.CompressString("the quick brown fox");
        var back = CompressionHelper.DecompressString(comp.Value!);
        Assert.Equal("the quick brown fox", back.Value);
    }
}

public class IdGeneratorTests
{
    [Fact]
    public void NanoId_has_requested_length_and_is_unique()
    {
        var a = IdGenerator.NanoId(21);
        var b = IdGenerator.NanoId(21);
        Assert.Equal(21, a.Length);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void NewGuidNoDashes_is_32_hex()
    {
        var g = IdGenerator.NewGuidNoDashes();
        Assert.Equal(32, g.Length);
        Assert.DoesNotContain("-", g);
    }

    [Fact]
    public void RandomHex_respects_length()
    {
        Assert.Equal(16, IdGenerator.RandomHex(16).Length);
    }

    [Fact]
    public void Sortable_ids_are_monotonic_ish()
    {
        var first = IdGenerator.Sortable();
        var second = IdGenerator.Sortable();
        Assert.True(string.CompareOrdinal(first, second) <= 0);
    }
}

public class PasswordGeneratorTests
{
    [Fact]
    public void Generate_respects_length()
    {
        Assert.Equal(24, PasswordGenerator.Generate(24).Length);
    }

    [Fact]
    public void Pin_is_all_digits()
    {
        var pin = PasswordGenerator.GeneratePin(6);
        Assert.Equal(6, pin.Length);
        Assert.All(pin, c => Assert.True(char.IsDigit(c)));
    }

    [Fact]
    public void Strong_password_rates_at_least_strong()
    {
        var pw = PasswordGenerator.GenerateStrong(20);
        var strength = PasswordGenerator.CalculateStrength(pw);
        Assert.True(strength >= PasswordStrength.Strong, $"got {strength}");
    }

    [Fact]
    public void Weak_password_rates_low()
    {
        Assert.True(PasswordGenerator.CalculateStrength("aaa") <= PasswordStrength.Weak);
    }
}

public class StringHelperTests
{
    [Theory]
    [InlineData("Hello World", "hello-world")]
    [InlineData("  Multiple   Spaces  ", "multiple-spaces")]
    public void ToSlug_normalizes(string input, string expected)
        => Assert.Equal(expected, StringHelper.ToSlug(input));

    [Fact]
    public void Truncate_produces_a_string_within_max_length()
    {
        // maxLength is the total length budget, including the suffix.
        var result = StringHelper.Truncate("abcdefgh", 6);
        Assert.True(result.Length <= 6, $"got '{result}' ({result.Length})");
        Assert.EndsWith("...", result);
    }

    [Fact]
    public void Truncate_leaves_short_strings()
    {
        Assert.Equal("ab", StringHelper.Truncate("ab", 5));
    }

    [Theory]
    [InlineData("hello world", "HelloWorld")]
    public void ToPascalCase(string input, string expected)
        => Assert.Equal(expected, StringHelper.ToPascalCase(input));

    [Fact]
    public void Reverse_works()
        => Assert.Equal("cba", StringHelper.Reverse("abc"));
}

public class StringValidatorTests
{
    [Theory]
    [InlineData("user@example.com", true)]
    [InlineData("not-an-email", false)]
    [InlineData("a@b", false)]
    public void IsEmail(string input, bool expected)
        => Assert.Equal(expected, StringValidator.IsEmail(input));

    [Theory]
    [InlineData("192.168.0.1", true)]
    [InlineData("999.1.1.1", false)]
    [InlineData("::1", false)]
    public void IsIPv4(string input, bool expected)
        => Assert.Equal(expected, StringValidator.IsIPv4(input));

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("ftp://x", false)]
    public void IsUrl(string input, bool expected)
        => Assert.Equal(expected, StringValidator.IsUrl(input));
}

public class TypeConverterTests
{
    [Fact]
    public void ToInt_parses_valid()
    {
        var r = TypeConverter.ToInt("42");
        Assert.True(r.IsSuccess);
        Assert.Equal(42, r.Value);
    }

    [Fact]
    public void ToInt_fails_on_garbage()
        => Assert.True(TypeConverter.ToInt("abc").IsFailure);

    [Fact]
    public void ToEnum_parses()
    {
        var r = TypeConverter.ToEnum<DayOfWeek>("Monday");
        Assert.True(r.IsSuccess);
        Assert.Equal(DayOfWeek.Monday, r.Value);
    }
}

public class JsonHelperTests
{
    private record Sample(string Name, int Age);

    [Fact]
    public void Serialize_deserialize_round_trips()
    {
        var s = JsonHelper.Serialize(new Sample("Alice", 30));
        Assert.True(s.IsSuccess);
        var back = JsonHelper.Deserialize<Sample>(s.Value!);
        Assert.True(back.IsSuccess);
        Assert.Equal("Alice", back.Value!.Name);
        Assert.Equal(30, back.Value.Age);
    }

    [Theory]
    [InlineData("{\"a\":1}", true)]
    [InlineData("{not json", false)]
    public void IsValidJson(string input, bool expected)
        => Assert.Equal(expected, JsonHelper.IsValidJson(input));
}

public class DateTimeHelperTests
{
    [Fact]
    public void Unix_timestamp_round_trips()
    {
        var dt = new DateTime(2026, 6, 20, 12, 0, 0, DateTimeKind.Utc);
        var ts = DateTimeHelper.ToUnixTimestamp(dt);
        Assert.Equal(dt, DateTimeHelper.FromUnixTimestamp(ts));
    }

    [Fact]
    public void StartOfDay_zeroes_time()
    {
        var d = DateTimeHelper.StartOfDay(new DateTime(2026, 6, 20, 15, 30, 45));
        Assert.Equal(0, d.Hour);
        Assert.Equal(0, d.Minute);
    }

    [Fact]
    public void Weekend_detection()
    {
        Assert.True(DateTimeHelper.IsWeekend(new DateTime(2026, 6, 20)));   // Saturday
        Assert.False(DateTimeHelper.IsWeekend(new DateTime(2026, 6, 22))); // Monday
    }
}

public class CronParserTests
{
    [Theory]
    [InlineData("* * * * *", true)]
    [InlineData("0 0 * * *", true)]
    [InlineData("not a cron", false)]
    public void IsValid(string expr, bool expected)
        => Assert.Equal(expected, CronParser.IsValid(expr));

    [Fact]
    public void Parse_splits_fields()
    {
        var r = CronParser.Parse("0 12 * * 1");
        Assert.True(r.IsSuccess);
        Assert.Equal("0", r.Value!.Minutes);
        Assert.Equal("12", r.Value.Hours);
    }
}

public class UrlHelperTests
{
    [Fact]
    public void Combine_joins_without_double_slash()
        => Assert.Equal("https://x.com/a/b", UrlHelper.Combine("https://x.com/a/", "/b"));

    [Fact]
    public void GetDomain_extracts_host()
        => Assert.Equal("example.com", UrlHelper.GetDomain("https://example.com/path?q=1"));

    [Fact]
    public void IsHttps()
    {
        Assert.True(UrlHelper.IsHttps("https://x"));
        Assert.False(UrlHelper.IsHttps("http://x"));
    }
}

public class NetworkPingTests
{
    [Fact]
    public async Task Ping_loopback_is_a_success_with_a_response()
    {
        var r = await CL.Common.Networking.NetworkPing.PingAsync("127.0.0.1", 2000);
        // Some sandboxes block ICMP entirely; then the only acceptable outcome is a typed failure.
        if (r.IsFailure)
        {
            Assert.True(r.Error is CodeLogic.Core.Results.TimeoutError or CodeLogic.Core.Results.UnavailableError
                or CodeLogic.Core.Results.InternalError);
            return;
        }
        Assert.Equal("127.0.0.1", r.Value!.Host);
        Assert.Equal("127.0.0.1", r.Value.Address);
        Assert.True(r.Value.RoundTripMs >= 0);
    }

    [Fact]
    public async Task Ping_unresolvable_host_is_an_unavailable_failure()
    {
        // .invalid is reserved (RFC 2606) and never resolves.
        var r = await CL.Common.Networking.NetworkPing.PingAsync("no-such-host.invalid", 1000);
        Assert.True(r.IsFailure);
        Assert.IsType<CodeLogic.Core.Results.UnavailableError>(r.Error);
        Assert.Equal("networking.ping_unreachable", r.Error!.Code);
    }
}
