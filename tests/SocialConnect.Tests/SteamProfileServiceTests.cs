using System.Net;
using CL.SocialConnect.Events;
using CL.SocialConnect.Models;
using CL.SocialConnect.Services.Steam;
using CodeLogic.Core.Events;
using CodeLogic.Core.Results;
using Xunit;

namespace SocialConnect.Tests;

/// <summary>
/// Steam profile reads, offline: the Steam Web API answer comes from a fake handler.
/// </summary>
public sealed class SteamProfileServiceTests
{
    private const string ApiKey = "SECRET-API-KEY-123";

    private sealed class FakeSteam(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return respond(request);
        }
    }

    private static string PlayerJson(string steamId) =>
        $$$"""{"response":{"players":[{"steamid":"{{{steamId}}}","personaname":"Player {{{steamId}}}","profileurl":"https://steamcommunity.com/id/x"}]}}""";

    private static FakeSteam AnswerPlayers() =>
        new(r => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(PlayerJson(System.Web.HttpUtility.ParseQueryString(r.RequestUri!.Query)["steamids"]!))
        });

    private static SteamProfileService Service(HttpMessageHandler handler, int maxCacheEntries = 10_000, TestLogger? logger = null, IEventBus? events = null) =>
        new(new SteamConfig { ApiKey = ApiKey, TimeoutSeconds = 5, MaxCacheEntries = maxCacheEntries }, handler, logger, events);

    private static string Id(int n) => "7656119796043" + (5530 + n).ToString("D4");

    [Fact]
    public async Task Player_is_fetched_once_and_then_served_from_the_cache()
    {
        var steam = AnswerPlayers();
        var events = new EventBus();
        var fetched = new List<SteamProfileFetchedEvent>();
        events.Subscribe<SteamProfileFetchedEvent>(fetched.Add);
        using var service = Service(steam, events: events);

        var first = await service.GetPlayerAsync(Id(0));
        var second = await service.GetPlayerAsync(Id(0));

        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.Equal(Id(0), second.Value!.SteamId);
        Assert.Single(steam.Requests);
        Assert.Equal([false, true], fetched.Select(e => e.FromCache));
    }

    [Fact]
    public async Task Cache_growth_is_bounded_and_the_oldest_entry_is_evicted()
    {
        var steam = AnswerPlayers();
        using var service = Service(steam, maxCacheEntries: 3);

        for (var i = 0; i < 4; i++)
            Assert.True((await service.GetPlayerAsync(Id(i))).IsSuccess);
        Assert.Equal(4, steam.Requests.Count);

        // The three newest are cached: no request.
        for (var i = 1; i < 4; i++)
            await service.GetPlayerAsync(Id(i));
        Assert.Equal(4, steam.Requests.Count);

        // The oldest was evicted to make room: fetched again.
        await service.GetPlayerAsync(Id(0));
        Assert.Equal(5, steam.Requests.Count);
    }

    [Fact]
    public async Task Cache_never_holds_more_than_the_configured_entries_under_churn()
    {
        var steam = AnswerPlayers();
        using var service = Service(steam, maxCacheEntries: 5);

        for (var i = 0; i < 100; i++)
            await service.GetPlayerAsync(Id(i));
        Assert.Equal(100, steam.Requests.Count);

        // Only the last 5 can still be cached; everything older must be fetched again.
        for (var i = 95; i < 100; i++)
            await service.GetPlayerAsync(Id(i));
        Assert.Equal(100, steam.Requests.Count);
        for (var i = 0; i < 95; i++)
            await service.GetPlayerAsync(Id(i));
        Assert.Equal(195, steam.Requests.Count);
    }

    [Fact]
    public async Task Api_key_is_in_the_query_but_never_in_logs_or_events()
    {
        var steam = AnswerPlayers();
        var logger = new TestLogger();
        var events = new EventBus();
        var published = new List<SteamProfileFetchedEvent>();
        events.Subscribe<SteamProfileFetchedEvent>(published.Add);
        using var service = Service(steam, logger: logger, events: events);

        await service.GetPlayerAsync(Id(0));
        await service.GetPlayerBansAsync(Id(0));
        await service.GetOwnedGamesAsync(Id(0));

        Assert.All(steam.Requests, u => Assert.Contains("key=" + ApiKey, u.Query));
        Assert.All(steam.Requests, u => Assert.Equal("https", u.Scheme));
        Assert.NotEmpty(logger.Lines);
        Assert.DoesNotContain(ApiKey, logger.All);
        Assert.NotEmpty(published);
        Assert.DoesNotContain(ApiKey, string.Join('\n', published.Select(e => e.ToString())));
    }

    [Fact]
    public async Task Cancelled_read_is_unavailable_with_the_cancelled_code()
    {
        using var cts = new CancellationTokenSource();
        var steam = new FakeSteam(_ => { cts.Cancel(); throw new OperationCanceledException(cts.Token); });
        using var service = Service(steam);

        var player = await service.GetPlayerAsync(Id(0), cts.Token);
        var bans = await service.GetPlayerBansAsync(Id(0), cts.Token);
        var games = await service.GetOwnedGamesAsync(Id(0), cancellationToken: cts.Token);

        foreach (var error in new[] { player.Error, bans.Error, games.Error })
        {
            Assert.IsType<UnavailableError>(error);
            Assert.Equal(SocialConnectErrorCodes.SteamCancelled, error!.Code);
        }
    }

    [Fact]
    public async Task Request_timeout_stays_a_timeout_error()
    {
        using var service = Service(new FakeSteam(_ => throw new TaskCanceledException("timed out", new TimeoutException())));
        var r = await service.GetPlayerAsync(Id(0));
        Assert.IsType<TimeoutError>(r.Error);
        Assert.Equal(SocialConnectErrorCodes.SteamTimeout, r.Error!.Code);
    }

    [Fact]
    public void Null_config_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => new SteamProfileService(null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("http://api.steampowered.com")]
    [InlineData("https://user:pw@api.steampowered.com")]
    [InlineData("https://api.steampowered.com/?x=1")]
    public void Bad_api_base_url_is_refused_by_the_services_and_by_validation(string url)
    {
        var config = new SteamConfig { ApiKey = "k", ApiBaseUrl = url };
        Assert.Throws<ArgumentException>(() => new SteamProfileService(config));
        Assert.Throws<ArgumentException>(() => new SteamAuthenticationService(config));
        Assert.False(new SocialConnectConfig { Steam = config }.Validate().IsValid);
    }

    [Theory]
    [InlineData("https://api.steampowered.com")]
    [InlineData("https://api.steampowered.com/")]
    [InlineData("https://proxy.example:8443/steam")]
    public void Https_api_base_url_is_accepted(string url)
    {
        var config = new SteamConfig { ApiKey = "k", ApiBaseUrl = url };
        Assert.True(config.TryGetApiBaseUri(out var uri, out _));
        Assert.EndsWith("/", uri!.AbsoluteUri);
        using var service = new SteamProfileService(config);
        Assert.True(new SocialConnectConfig { Steam = config }.Validate().IsValid);
    }
}
