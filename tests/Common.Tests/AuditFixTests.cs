using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using CL.Common.Caching;
using CL.Common.Generators;
using CL.Common.Parser;
using CL.Common.Security;
using CL.Common.Web;
using CodeLogic.Core.Results;
using Xunit;

namespace Common.Tests;

// Regression tests for the 2026-11 audit fixes (second round) in CL.Common.

public class HtmlSanitizeScalingTests
{
    [Theory]
    [InlineData("<a ")]       // every '<' starts a tag that never closes
    [InlineData("<a \" ")]    // unbalanced quotes on top
    [InlineData("<")]
    [InlineData("<!-- ")]
    public void Large_pathological_input_is_sanitized_in_linear_time(string unit)
    {
        var input = string.Concat(Enumerable.Repeat(unit, 220_000 / unit.Length));   // >= 200 KB
        Assert.True(input.Length >= 200_000);

        var watch = Stopwatch.StartNew();
        var output = HtmlHelper.Sanitize(input);
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed}");
        Assert.DoesNotContain('<', output);
    }

    [Fact]
    public void StripTags_handles_a_long_run_of_angle_brackets_quickly()
    {
        var input = new string('<', 300_000);
        var watch = Stopwatch.StartNew();
        Assert.Equal(input, HtmlHelper.StripTags(input));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed}");
    }

    [Theory]
    [InlineData("<!--a--><b>x</b><!--c-->", "<b>x</b>")]                              // two comments stay two comments
    [InlineData("<!-- open comment <b>x</b>", "")]                                     // an unterminated comment eats the rest
    [InlineData("<b title=\"a>b\">x</b>", "<b>x</b>")]                                 // '>' inside quotes does not end the tag
    [InlineData("<b title=\"unterminated>x</b>", "&lt;b title=&quot;unterminated&gt;x</b>")]  // no closing quote: not a tag
    [InlineData("<b<i>x</i>", "<b>x</i>")]                                             // '<' inside a tag is attribute text
    [InlineData("a < b <b>c</b>", "a &lt; b <b>c</b>")]
    [InlineData("<br/><hr />", "<br><hr>")]
    [InlineData("<script/>after", "after")]                                            // a self-closing dropped tag drops nothing after it
    [InlineData("<script>never</script>", "")]
    [InlineData("<1>", "&lt;1&gt;")]
    public void Scanner_matches_the_documented_tag_rules(string input, string expected) =>
        Assert.Equal(expected, HtmlHelper.Sanitize(input));
}

public class AcceptLanguageTests
{
    [Theory]
    [InlineData("da-DK,da;q=0.9,en-US;q=0.8", "da-DK")]
    [InlineData("en", "en")]
    [InlineData("zh-Hant-TW", "zh-Hant-TW")]
    [InlineData("en-US;q=0.5, fr-FR;q=0.9", "fr-FR")]
    [InlineData("*", "en-US")]
    [InlineData("*, de;q=0.5", "de")]
    [InlineData("<script>alert(1)</script>", "en-US")]
    [InlineData("en-US\r\nX-Injected: 1", "en-US")]
    [InlineData("en_US", "en-US")]                        // underscore is not BCP 47
    [InlineData("toolongprimary-x", "en-US")]
    [InlineData("' OR 1=1 --,da", "da")]
    [InlineData("", "en-US")]
    [InlineData(null, "en-US")]
    public void Only_well_formed_language_tags_are_returned(string? header, string expected) =>
        Assert.Equal(expected, HttpHeaderHelper.GetPrimaryLocale(header));

    [Fact]
    public void Bot_and_mobile_detection_still_work_with_generated_regexes()
    {
        Assert.True(HttpHeaderHelper.IsBot("Mozilla/5.0 (compatible; Googlebot/2.1)"));
        Assert.False(HttpHeaderHelper.IsBot("Mozilla/5.0 (Windows NT 10.0) Chrome/120"));
        Assert.True(HttpHeaderHelper.IsMobile("Mozilla/5.0 (iPhone; CPU iPhone OS 17_0)"));
        Assert.False(HttpHeaderHelper.IsMobile("Mozilla/5.0 (Windows NT 10.0) Chrome/120"));
        Assert.False(HttpHeaderHelper.IsMobile(null));
    }
}

public class EncryptionFormatTests
{
    private const string Password = "correct horse battery staple";

    /// <summary>A blob in the pre-5.0 layout: salt + nonce + tag + ciphertext, 100 000 iterations, no version byte.</summary>
    private static byte[] LegacyEncrypt(byte[] data, string password, byte? forceSaltFirstByte = null)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        if (forceSaltFirstByte is { } b) salt[0] = b;
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Encryption.LegacyIterations, HashAlgorithmName.SHA256, 32);
        var ciphertext = new byte[data.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, data, ciphertext, tag);
        return [.. salt, .. nonce, .. tag, .. ciphertext];
    }

    [Fact]
    public void New_blobs_carry_the_version_byte_and_600k_iterations()
    {
        var blob = Encryption.EncryptBytes("hello"u8.ToArray(), Password);

        Assert.Equal(1, blob[0]);
        Assert.Equal(1 + 16 + 12 + 16 + 5, blob.Length);
        Assert.Equal(600_000, Encryption.Iterations);
        Assert.Equal("hello", Encoding.UTF8.GetString(Encryption.DecryptBytes(blob, Password)));
        Assert.Equal("round trip", Encryption.DecryptAes(Encryption.EncryptAes("round trip", Password), Password));
    }

    [Fact]
    public void Legacy_blobs_still_decrypt()
    {
        var legacy = LegacyEncrypt("old data"u8.ToArray(), Password);
        Assert.Equal("old data", Encoding.UTF8.GetString(Encryption.DecryptBytes(legacy, Password)));
        Assert.Equal("old data", Encryption.DecryptAes(Convert.ToBase64String(legacy), Password));
    }

    [Fact]
    public void A_legacy_blob_whose_salt_starts_with_the_version_byte_still_decrypts()
    {
        var legacy = LegacyEncrypt("ambiguous"u8.ToArray(), Password, forceSaltFirstByte: 1);
        Assert.Equal("ambiguous", Encoding.UTF8.GetString(Encryption.DecryptBytes(legacy, Password)));
    }

    [Fact]
    public void Wrong_password_fails_for_both_formats()
    {
        Assert.ThrowsAny<CryptographicException>(() => Encryption.DecryptBytes(Encryption.EncryptBytes([1, 2, 3], Password), "nope"));
        Assert.ThrowsAny<CryptographicException>(() => Encryption.DecryptBytes(LegacyEncrypt([1, 2, 3], Password), "nope"));
    }

    [Theory]
    [InlineData(0)]    // version byte
    [InlineData(5)]    // salt
    [InlineData(20)]   // nonce
    [InlineData(35)]   // tag
    [InlineData(46)]   // ciphertext
    public void Tampering_with_any_byte_is_detected(int index)
    {
        var blob = Encryption.EncryptBytes("payload!"u8.ToArray(), Password);
        blob[index] ^= 0x01;
        Assert.ThrowsAny<CryptographicException>(() => Encryption.DecryptBytes(blob, Password));
    }

    [Fact]
    public void Truncated_blobs_are_rejected()
    {
        var blob = Encryption.EncryptBytes("payload!"u8.ToArray(), Password);
        Assert.ThrowsAny<CryptographicException>(() => Encryption.DecryptBytes(blob[..10], Password));
        Assert.ThrowsAny<CryptographicException>(() => Encryption.DecryptBytes(blob[..44], Password));
        Assert.ThrowsAny<CryptographicException>(() => Encryption.DecryptBytes(blob[..^1], Password));
        Assert.ThrowsAny<CryptographicException>(() => Encryption.DecryptBytes([], Password));
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("AAAA")]
    [InlineData("")]
    public void Malformed_text_is_a_cryptographic_exception_not_a_format_exception(string cipherText) =>
        Assert.ThrowsAny<CryptographicException>(() => Encryption.DecryptAes(cipherText, Password));
}

public class CronPreCheckTests
{
    [Theory]
    [InlineData("99 * * * *")]
    [InlineData("0 24 * * *")]
    [InlineData("0 0 0 * *")]
    [InlineData("0 0 * 13 *")]
    [InlineData("0 0 32 * 7")]       // both day fields restricted and both impossible
    [InlineData("0 0 */0 * *")]      // a zero step never matches
    public void An_impossible_field_fails_at_once_with_a_validation_error(string expression)
    {
        var watch = Stopwatch.StartNew();
        var utc = CronParser.GetNextOccurrence(expression, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));
        var zoned = CronParser.GetNextOccurrence(expression, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), null);

        Assert.IsType<ValidationError>(utc.Error);
        Assert.IsType<ValidationError>(zoned.Error);
        Assert.Contains("never", utc.Error!.Message);
        Assert.True(watch.ElapsedMilliseconds < 2_000);
    }

    [Fact]
    public void A_possible_day_of_week_rescues_an_impossible_day_of_month_when_both_are_restricted()
    {
        // 32nd of the month never, but Mondays do: the fields are ORed, so it fires.
        var next = CronParser.GetNextOccurrence("0 0 32 * 1", new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc)).ValueOrThrow();
        Assert.Equal(DayOfWeek.Monday, next.DayOfWeek);
    }

    [Fact]
    public void Expressions_that_can_fire_are_unaffected() =>
        Assert.True(CronParser.GetNextOccurrence("*/15 9-17 * * 1-5", new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc)).IsSuccess);
}

public class SmallFixTests
{
    [Fact]
    public void MemoryCache_Set_reports_whether_the_entry_was_stored()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 100, ExpirationScanInterval = Timeout.InfiniteTimeSpan });

        Assert.True(cache.Set("a", 1, new CacheEntryOptions { Size = 60 }));
        Assert.False(cache.Set("huge", 2, new CacheEntryOptions { Size = 101 }));
        Assert.False(cache.TryGet<int>("huge", out _));
        Assert.True(cache.Set("a", 3, new CacheEntryOptions { Size = 100 }));   // replacing at exactly the limit is fine
        Assert.False(cache.Set("a", 4, new CacheEntryOptions { Size = 101 }));
        Assert.False(cache.TryGet<int>("a", out _), "the previous entry under the key is removed either way");
    }

    [Fact]
    public void ParseQuery_decodes_plus_as_space_and_ignores_the_fragment()
    {
        var q = UrlHelper.ParseQuery("https://x.test/p?q=a+b&name=J%C3%B8rgen+Hansen&raw=%2B&flag#frag=ment");

        Assert.Equal("a b", q["q"]);
        Assert.Equal("Jørgen Hansen", q["name"]);
        Assert.Equal("+", q["raw"]);
        Assert.Equal("", q["flag"]);
        Assert.False(q.ContainsKey("frag"));
    }

    [Fact]
    public void Combine_treats_null_as_empty()
    {
        Assert.Equal("https://x.test/", UrlHelper.Combine("https://x.test/", null));
        Assert.Equal("/a", UrlHelper.Combine(null, "/a"));
        Assert.Equal("https://x.test/a", UrlHelper.Combine("https://x.test", "a"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void RandomHex_rejects_non_positive_lengths(int length) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => IdGenerator.RandomHex(length));

    [Fact]
    public void RandomHex_odd_lengths_are_exact() => Assert.Equal(7, IdGenerator.RandomHex(7).Length);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }

    [Fact]
    public async Task Request_headers_are_validated_content_headers_go_on_the_content_and_lenient_ones_are_passed_through()
    {
        var handler = new CapturingHandler();
        using var client = new HttpClient(handler);

        var ok = await HttpClientHelper.PostJsonAsync(client, "http://example.test/", new { a = 1 }, new Dictionary<string, string>
        {
            ["X-Trace"] = "abc123",
            ["Content-Language"] = "da",
            ["User-Agent"] = "my app (with spaces and (parens)) v1"
        });

        Assert.True(ok.IsSuccess, ok.Error?.Message);
        Assert.Equal("abc123", Assert.Single(handler.Request!.Headers.GetValues("X-Trace")));
        Assert.Equal("da", Assert.Single(handler.Request.Content!.Headers.GetValues("Content-Language")));
        Assert.Contains("my app", handler.Request.Headers.UserAgent.ToString());

        var injected = await HttpClientHelper.GetStringAsync(client, "http://example.test/", new Dictionary<string, string>
        {
            ["X-Evil"] = "value\r\nX-Injected: 1"
        });
        Assert.True(injected.IsFailure, "a header value with a line break must be rejected, not sent");
    }
}
