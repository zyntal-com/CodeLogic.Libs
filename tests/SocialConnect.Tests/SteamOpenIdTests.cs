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

    private static SteamOpenIdService Service(HttpMessageHandler handler, int maxNonceAgeSeconds = 300, string realm = "",
        IOpenIdNonceStore? nonces = null, TestLogger? logger = null, CodeLogic.Core.Events.IEventBus? events = null) =>
        new(new SteamConfig { TimeoutSeconds = 5, OpenIdNonceMaxAgeSeconds = maxNonceAgeSeconds, OpenIdRealm = realm }, handler, logger, events, nonces);

    private static int _nonceCounter;

    // Every assertion gets its own nonce: the service refuses one it has verified before.
    private static string Nonce(TimeSpan age, string? unique = null) =>
        (DateTime.UtcNow - age).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture)
        + (unique ?? "n" + Interlocked.Increment(ref _nonceCounter));

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
    [InlineData("https://example.com/apple/cb", "https://example.com/app")]    // same prefix, other directory
    [InlineData("https://example.com:8443/cb", "https://example.com")]
    [InlineData("http://example.com/cb", "https://example.com")]
    public void BuildLoginUrl_rejects_bad_urls(string returnTo, string? realm)
    {
        using var service = Service(Answer(""));
        Assert.Throws<ArgumentException>(() => service.BuildLoginUrl(returnTo, realm));
    }

    [Theory]
    [InlineData("https://evil.example/cb")]
    [InlineData("https://example.com.evil.example/cb")]
    [InlineData("http://example.com/cb")]
    [InlineData("https://example.com/other/cb")]
    public void Configured_realm_refuses_a_return_url_outside_it(string returnTo)
    {
        using var service = Service(Answer(""), realm: "https://example.com/auth");
        Assert.Throws<ArgumentException>(() => service.BuildLoginUrl(returnTo));
        // A realm argument cannot widen the configured one.
        Assert.Throws<ArgumentException>(() => service.BuildLoginUrl(returnTo, "https://evil.example"));
    }

    [Fact]
    public void Configured_realm_is_sent_to_steam_and_contains_the_return_url()
    {
        using var service = Service(Answer(""), realm: "https://example.com/auth");
        var query = Uri.UnescapeDataString(new Uri(service.BuildLoginUrl("https://example.com/auth/steam/cb?state=x")).Query);
        Assert.Contains("openid.realm=https://example.com/auth", query);
        Assert.Contains("openid.return_to=https://example.com/auth/steam/cb?state=x", query);
    }

    [Fact]
    public void Without_a_realm_the_return_url_decides_the_site()
    {
        // Documented behaviour: no OpenIdRealm and no realm argument means any absolute URL is accepted.
        using var service = Service(Answer(""));
        Assert.Contains("openid.realm=https%3A%2F%2Fanything.example%2F", service.BuildLoginUrl("https://anything.example/cb"));
    }

    [Fact]
    public void Invalid_configured_realm_is_a_config_error_and_refused_by_the_service()
    {
        var config = new SocialConnectConfig { Steam = { Enabled = true, OpenIdRealm = "example.com" } };
        Assert.False(config.Validate().IsValid);
        Assert.Throws<ArgumentException>(() => new SteamOpenIdService(config.Steam));
    }

    [Fact]
    public void CreateState_is_random_and_url_safe()
    {
        var states = Enumerable.Range(0, 50).Select(_ => SteamOpenIdService.CreateState()).ToList();
        Assert.Equal(50, states.Distinct(StringComparer.Ordinal).Count());
        foreach (var state in states)
        {
            Assert.Equal(43, state.Length);   // 32 bytes = 256 bits, base64url without padding
            Assert.All(state, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '_', $"unexpected '{c}'"));
            Assert.Equal(state, Uri.EscapeDataString(state));
        }
    }

    // ── Replay ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Verify_refuses_the_same_assertion_a_second_time()
    {
        var steam = Answer("is_valid:true");
        using var service = Service(steam);
        var assertion = Assertion();

        var first = await service.VerifyAsync(assertion, ReturnTo);
        var second = await service.VerifyAsync(assertion, ReturnTo);

        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.True(second.IsFailure);
        Assert.Equal(SocialConnectErrorCodes.SteamOpenIdReplayed, second.Error!.Code);
        Assert.Equal(2, steam.Bodies.Count);   // Only authenticated assertions reach the atomic replay check.
    }

    [Fact]
    public async Task Verify_does_not_spend_a_nonce_when_steam_rejects_the_assertion()
    {
        var answers = new Queue<string>(["is_valid:false", "is_valid:true"]);
        using var service = Service(new FakeSteam(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answers.Dequeue()) }));
        var assertion = Assertion();

        Assert.Equal(SocialConnectErrorCodes.SteamOpenIdRejected, (await service.VerifyAsync(assertion, ReturnTo)).Error!.Code);
        Assert.True((await service.VerifyAsync(assertion, ReturnTo)).IsSuccess);
    }

    [Fact]
    public async Task Nonce_store_is_shared_between_services_when_given()
    {
        var nonces = new InMemoryOpenIdNonceStore();
        using var a = Service(Answer("is_valid:true"), nonces: nonces);
        using var b = Service(Answer("is_valid:true"), nonces: nonces);
        var assertion = Assertion();

        Assert.True((await a.VerifyAsync(assertion, ReturnTo)).IsSuccess);
        Assert.Equal(SocialConnectErrorCodes.SteamOpenIdReplayed, (await b.VerifyAsync(assertion, ReturnTo)).Error!.Code);
        Assert.Equal(1, nonces.Count);
    }

    [Fact]
    public async Task In_memory_nonce_store_forgets_expired_nonces()
    {
        var store = new InMemoryOpenIdNonceStore(sweepInterval: TimeSpan.Zero);
        Assert.True(await store.TryUseAsync("n1", DateTime.UtcNow.AddMinutes(5)));
        Assert.False(await store.TryUseAsync("n1", DateTime.UtcNow.AddMinutes(5)));
        Assert.True(await store.TryUseAsync("old", DateTime.UtcNow.AddMinutes(-1)));
        Assert.True(await store.TryUseAsync("old", DateTime.UtcNow.AddMinutes(5)));   // expired: as good as unseen
        Assert.True(await store.TryUseAsync("n2", DateTime.UtcNow.AddMinutes(5)));
        Assert.Equal(3, store.Count);
    }

    [Fact]
    public async Task Failed_assertions_never_consume_nonce_capacity()
    {
        var store = new InMemoryOpenIdNonceStore(maxEntries: 2);
        using var service = Service(Answer("is_valid:false"), nonces: store);
        for (var i = 0; i < 100; i++) Assert.True((await service.VerifyAsync(Assertion(), ReturnTo)).IsFailure);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task Replay_capacity_is_atomic_and_does_not_evict_unexpired_entries()
    {
        var store = new InMemoryOpenIdNonceStore(maxEntries: 1);
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(async () =>
            await store.TryUseAsync("n" + i, DateTime.UtcNow.AddMinutes(5)))));
        Assert.Single(results, r => r);
        Assert.Equal(1, store.Count);
        Assert.False(await store.TryUseAsync("extra", DateTime.UtcNow.AddMinutes(5)));
    }

    // ── Hostile input ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Repeated_parameter_name_is_sanitised_before_it_reaches_logs_events_or_the_error()
    {
        var logger = new TestLogger();
        var bus = new CodeLogic.Core.Events.EventBus();
        var events = new List<CL.SocialConnect.Events.SteamOpenIdVerifiedEvent>();
        bus.Subscribe<CL.SocialConnect.Events.SteamOpenIdVerifiedEvent>(events.Add);
        using var service = Service(Answer("is_valid:true"), logger: logger, events: bus);
        var hostile = "openid.x\r\nERROR forged line" + new string('A', 200);
        var query = "openid.mode=id_res&" + Uri.EscapeDataString(hostile) + "=1&" + Uri.EscapeDataString(hostile) + "=2";

        var result = await service.VerifyAsync(query, ReturnTo);

        Assert.Equal(SocialConnectErrorCodes.SteamOpenIdInvalid, result.Error!.Code);
        foreach (var text in new[] { result.Error.Message, logger.All, Assert.Single(events).ErrorMessage! })
        {
            Assert.DoesNotContain("\n", text);
            Assert.DoesNotContain("\r", text);
            Assert.DoesNotContain(new string('A', 100), text);
            Assert.Contains("openid.x??ERROR forged line", text);
        }
    }

    [Fact]
    public async Task Cancelled_verification_is_unavailable_with_the_cancelled_code()
    {
        using var cts = new CancellationTokenSource();
        using var service = Service(new FakeSteam(_ => { cts.Cancel(); throw new OperationCanceledException(cts.Token); }));

        var result = await service.VerifyAsync(Assertion(), ReturnTo, cts.Token);

        Assert.IsType<CodeLogic.Core.Results.UnavailableError>(result.Error);
        Assert.Equal(SocialConnectErrorCodes.SteamCancelled, result.Error!.Code);
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
        Assert.Equal(SocialConnectErrorCodes.SteamOpenIdExpired, result.Error!.Code);
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
        Assert.Equal(SocialConnectErrorCodes.SteamOpenIdInvalid, result.Error!.Code);
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
        Assert.Equal(SocialConnectErrorCodes.SteamOpenIdInvalid, result.Error!.Code);
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
        Assert.Equal(SocialConnectErrorCodes.SteamOpenIdRejected, result.Error!.Code);
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
        { "openid.return_to", "https://example.com/auth/steam/callback?state=other", SocialConnectErrorCodes.SteamOpenIdReturnTo },
        { "openid.op_endpoint", "https://evil.example/openid/login", SocialConnectErrorCodes.SteamOpenIdInvalid },
        { "openid.ns", "http://openid.net/signon/1.1", SocialConnectErrorCodes.SteamOpenIdInvalid },
        { "openid.mode", "cancel", SocialConnectErrorCodes.SteamOpenIdCancelled },
        { "openid.mode", "check_authentication", SocialConnectErrorCodes.SteamOpenIdInvalid },
        { "openid.identity", "https://steamcommunity.com/openid/id/76561197960435531", SocialConnectErrorCodes.SteamOpenIdIdentity },
        { "openid.signed", "signed,op_endpoint,claimed_id,identity,response_nonce", SocialConnectErrorCodes.SteamOpenIdInvalid },
        { "openid.sig", "", SocialConnectErrorCodes.SteamOpenIdInvalid }
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
        Assert.Equal(SocialConnectErrorCodes.SteamOpenIdIdentity, result.Error!.Code);
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
        Assert.Equal(SocialConnectErrorCodes.SteamOpenIdUnavailable, result.Error!.Code);
    }

    [Fact]
    public async Task Verify_needs_an_expected_return_to()
    {
        using var service = Service(Answer("is_valid:true"));
        var result = await service.VerifyAsync(Assertion(), "");
        Assert.True(result.IsFailure);
        Assert.Equal(SocialConnectErrorCodes.SteamOpenIdReturnTo, result.Error!.Code);
    }
}
