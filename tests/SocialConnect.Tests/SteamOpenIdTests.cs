using System.Net;
using CL.SocialConnect.Models;
using CL.SocialConnect.Services.Steam;
using Xunit;

namespace SocialConnect.Tests;

/// <summary>
/// Steam OpenID sign-in, offline: Steam's <c>check_authentication</c> answer comes from a fake handler.
/// </summary>
public sealed class SteamOpenIdTests
{
    private const string ReturnTo = "https://example.com/auth/steam/callback?state=abc123";
    private const string SteamId = "76561197960435530";

    private sealed class FakeSteam(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return respond(request);
        }
    }

    private static FakeSteam Answer(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(_ => new HttpResponseMessage(status) { Content = new StringContent(body) });

    private static SteamOpenIdService Service(HttpMessageHandler handler, int maxNonceAgeSeconds = 300) =>
        new(new SteamConfig { TimeoutSeconds = 5, OpenIdNonceMaxAgeSeconds = maxNonceAgeSeconds }, handler);

    private static string Nonce(TimeSpan age, string unique = "abc") =>
        (DateTime.UtcNow - age).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture) + unique;

    private static Dictionary<string, string> Assertion(string? claimedId = null, string? nonce = null) => new()
    {
        ["openid.ns"] = "http://specs.openid.net/auth/2.0",
        ["openid.mode"] = "id_res",
        ["openid.op_endpoint"] = SteamOpenIdService.Endpoint,
        ["openid.claimed_id"] = claimedId ?? "https://steamcommunity.com/openid/id/" + SteamId,
        ["openid.identity"] = claimedId ?? "https://steamcommunity.com/openid/id/" + SteamId,
        ["openid.return_to"] = ReturnTo,
        ["openid.response_nonce"] = nonce ?? Nonce(TimeSpan.FromSeconds(5)),
        ["openid.assoc_handle"] = "1234567890",
        ["openid.signed"] = "signed,op_endpoint,claimed_id,identity,return_to,response_nonce,assoc_handle",
        ["openid.sig"] = "c2lnbmF0dXJl"
    };

    // ── BuildLoginUrl ─────────────────────────────────────────────────────────

    [Fact]
    public void BuildLoginUrl_points_at_steam_with_return_to_and_realm()
    {
        using var service = Service(Answer(""));
        var url = new Uri(service.BuildLoginUrl(ReturnTo));

        Assert.Equal("https://steamcommunity.com/openid/login", url.GetLeftPart(UriPartial.Path));
        var query = Uri.UnescapeDataString(url.Query);
        Assert.Contains("openid.mode=checkid_setup", query);
        Assert.Contains("openid.return_to=" + ReturnTo, query);
        Assert.Contains("openid.realm=https://example.com/", query);
        Assert.Contains("openid.claimed_id=http://specs.openid.net/auth/2.0/identifier_select", query);
    }

    [Theory]
    [InlineData("/auth/steam/callback", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("https://example.com/cb#frag", null)]
    [InlineData("https://evil.example/cb", "https://example.com")]
    public void BuildLoginUrl_rejects_bad_urls(string returnTo, string? realm)
    {
        using var service = Service(Answer(""));
        Assert.Throws<ArgumentException>(() => service.BuildLoginUrl(returnTo, realm));
    }

    // ── VerifyAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Verify_returns_the_steam_id_when_steam_confirms()
    {
        var steam = Answer("ns:http://specs.openid.net/auth/2.0\nis_valid:true\n");
        using var service = Service(steam);

        var result = await service.VerifyAsync(Assertion(), ReturnTo);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(SteamId, result.Value!.SteamId);
        var body = Uri.UnescapeDataString(Assert.Single(steam.Bodies));
        Assert.Contains("openid.mode=check_authentication", body);
        Assert.DoesNotContain("openid.mode=id_res", body);
    }

    [Fact]
    public async Task Verify_exposes_the_response_nonce_and_its_issue_time()
    {
        var nonce = Nonce(TimeSpan.FromSeconds(30), "Zx9-unique");
        using var service = Service(Answer("is_valid:true"));

        var result = await service.VerifyAsync(Assertion(nonce: nonce), ReturnTo);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(nonce, result.Value!.ResponseNonce);
        Assert.Equal(DateTimeKind.Utc, result.Value.NonceIssuedAtUtc.Kind);
        Assert.InRange(DateTime.UtcNow - result.Value.NonceIssuedAtUtc, TimeSpan.FromSeconds(29), TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task Verify_refuses_an_assertion_older_than_the_max_age_without_asking_steam()
    {
        var steam = Answer("is_valid:true");
        using var service = Service(steam, maxNonceAgeSeconds: 300);

        var result = await service.VerifyAsync(Assertion(nonce: Nonce(TimeSpan.FromMinutes(10))), ReturnTo);

        Assert.True(result.IsFailure);
        Assert.Equal("social.steam.openid_expired", result.Error!.Code);
        Assert.Empty(steam.Bodies);
    }

    [Fact]
    public async Task Verify_accepts_an_old_assertion_when_the_age_check_is_off()
    {
        using var service = Service(Answer("is_valid:true"), maxNonceAgeSeconds: 0);

        var result = await service.VerifyAsync(Assertion(nonce: "2020-01-01T00:00:00Zold"), ReturnTo);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), result.Value!.NonceIssuedAtUtc);
    }

    [Fact]
    public async Task Verify_refuses_an_assertion_dated_in_the_future()
    {
        using var service = Service(Answer("is_valid:true"));

        var result = await service.VerifyAsync(Assertion(nonce: Nonce(TimeSpan.FromMinutes(-5))), ReturnTo);

        Assert.True(result.IsFailure);
        Assert.Equal("social.steam.openid_invalid", result.Error!.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("2026-13-01T00:00:00Zabc")]
    [InlineData("2026-10-31 12:00:00Zabc")]
    [InlineData("2026-10-31T12:00:00+01:00")]
    public async Task Verify_refuses_a_malformed_nonce(string nonce)
    {
        var steam = Answer("is_valid:true");
        using var service = Service(steam, maxNonceAgeSeconds: 0);

        var result = await service.VerifyAsync(Assertion(nonce: nonce), ReturnTo);

        Assert.True(result.IsFailure);
        Assert.Equal("social.steam.openid_invalid", result.Error!.Code);
        Assert.Empty(steam.Bodies);
    }

    [Fact]
    public void Negative_max_nonce_age_is_a_config_error()
    {
        var config = new SocialConnectConfig { Steam = { OpenIdNonceMaxAgeSeconds = -1 } };
        Assert.False(config.Validate().IsValid);
    }

    [Fact]
    public async Task Verify_accepts_the_raw_query_string()
    {
        using var service = Service(Answer("is_valid:true"));
        var query = "?" + string.Join('&', Assertion().Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));

        var result = await service.VerifyAsync(query, ReturnTo);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(SteamId, result.Value!.SteamId);
    }

    [Theory]
    [InlineData("is_valid:false")]
    [InlineData("is_valid:true-ish")]
    [InlineData("")]
    public async Task Verify_fails_when_steam_does_not_confirm(string body)
    {
        using var service = Service(Answer(body));
        var result = await service.VerifyAsync(Assertion(), ReturnTo);
        Assert.True(result.IsFailure);
        Assert.Equal("social.steam.openid_rejected", result.Error!.Code);
    }

    [Fact]
    public async Task Verify_fails_on_http_error_even_with_is_valid_true()
    {
        using var service = Service(Answer("is_valid:true", HttpStatusCode.InternalServerError));
        var result = await service.VerifyAsync(Assertion(), ReturnTo);
        Assert.True(result.IsFailure);
    }

    public static TheoryData<string, string, string> Tampered => new()
    {
        { "openid.return_to", "https://example.com/auth/steam/callback?state=other", "social.steam.openid_return_to" },
        { "openid.op_endpoint", "https://evil.example/openid/login", "social.steam.openid_invalid" },
        { "openid.ns", "http://openid.net/signon/1.1", "social.steam.openid_invalid" },
        { "openid.mode", "cancel", "social.steam.openid_cancelled" },
        { "openid.mode", "check_authentication", "social.steam.openid_invalid" },
        { "openid.identity", "https://steamcommunity.com/openid/id/76561197960435531", "social.steam.openid_identity" },
        { "openid.signed", "signed,op_endpoint,claimed_id,identity,response_nonce", "social.steam.openid_invalid" },
        { "openid.sig", "", "social.steam.openid_invalid" }
    };

    [Theory]
    [MemberData(nameof(Tampered))]
    public async Task Verify_refuses_tampered_assertions_without_asking_steam(string key, string value, string code)
    {
        var steam = Answer("is_valid:true");
        using var service = Service(steam);
        var assertion = Assertion();
        assertion[key] = value;

        var result = await service.VerifyAsync(assertion, ReturnTo);

        Assert.True(result.IsFailure);
        Assert.Equal(code, result.Error!.Code);
        Assert.Empty(steam.Bodies);
    }

    [Theory]
    [InlineData("https://steamcommunity.com/openid/id/123")]
    [InlineData("https://steamcommunity.com/openid/id/12345678901234567")]
    [InlineData("http://steamcommunity.com/openid/id/76561197960435530")]
    [InlineData("https://steamcommunity.com.evil.example/openid/id/76561197960435530")]
    [InlineData("https://steamcommunity.com/openid/id/76561197960435530/")]
    public async Task Verify_refuses_malformed_claimed_ids(string claimedId)
    {
        var steam = Answer("is_valid:true");
        using var service = Service(steam);

        var result = await service.VerifyAsync(Assertion(claimedId), ReturnTo);

        Assert.True(result.IsFailure);
        Assert.Equal("social.steam.openid_identity", result.Error!.Code);
        Assert.Empty(steam.Bodies);
    }

    [Fact]
    public async Task Verify_refuses_repeated_openid_parameters()
    {
        var steam = Answer("is_valid:true");
        using var service = Service(steam);
        var parameters = Assertion().ToList();
        parameters.Add(new("openid.claimed_id", "https://steamcommunity.com/openid/id/76561197960435531"));

        var result = await service.VerifyAsync(parameters, ReturnTo);

        Assert.True(result.IsFailure);
        Assert.Empty(steam.Bodies);
    }

    [Fact]
    public async Task Verify_ignores_non_openid_parameters()
    {
        var steam = Answer("is_valid:true");
        using var service = Service(steam);
        var parameters = Assertion();
        parameters["state"] = "abc123";

        var result = await service.VerifyAsync(parameters, ReturnTo);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.DoesNotContain("state=", Assert.Single(steam.Bodies));
    }

    [Fact]
    public async Task Verify_reports_steam_unreachable()
    {
        using var service = Service(new FakeSteam(_ => throw new HttpRequestException("connection refused")));
        var result = await service.VerifyAsync(Assertion(), ReturnTo);
        Assert.True(result.IsFailure);
        Assert.Equal("social.steam.openid_unavailable", result.Error!.Code);
    }

    [Fact]
    public async Task Verify_needs_an_expected_return_to()
    {
        using var service = Service(Answer("is_valid:true"));
        var result = await service.VerifyAsync(Assertion(), "");
        Assert.True(result.IsFailure);
        Assert.Equal("social.steam.openid_return_to", result.Error!.Code);
    }
}
