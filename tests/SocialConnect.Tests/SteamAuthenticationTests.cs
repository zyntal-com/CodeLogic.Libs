using System.Net;
using CL.SocialConnect.Models;
using CL.SocialConnect.Models.Steam;
using CL.SocialConnect.Services.Steam;
using CodeLogic.Core.Results;
using Xunit;

namespace SocialConnect.Tests;

/// <summary>
/// Steam ticket authentication, offline: the Steam Web API answer comes from a fake handler.
/// </summary>
public sealed class SteamAuthenticationTests
{
    private const string SteamId = "76561197960435530";

    private sealed class FakeSteam(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private static FakeSteam Answer(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(_ => new HttpResponseMessage(status) { Content = new StringContent(body) });

    private static SteamAuthenticationService Service(HttpMessageHandler handler, string appId = "480") =>
        new(new SteamConfig { ApiKey = "KEY", AppId = appId, TimeoutSeconds = 5 }, handler);

    [Fact]
    public async Task Accepted_ticket_returns_the_identity()
    {
        var steam = Answer(
            """{"response":{"params":{"result":"OK","steamid":"SID","ownersteamid":"76561197960000001","vacbanned":false,"publisherbanned":true}}}"""
                .Replace("SID", SteamId));
        using var service = Service(steam);

        var r = await service.AuthenticateAsync("DEADBEEF");

        Assert.True(r.IsSuccess);
        Assert.Equal(new SteamTicketIdentity(SteamId, "76561197960000001", "480", false, true), r.Value);
        Assert.True(r.Value!.IsFamilyShared);
        Assert.Contains("ticket=DEADBEEF", steam.Requests.Single().Query);
        Assert.Contains("appid=480", steam.Requests.Single().Query);
    }

    [Fact]
    public async Task Per_call_app_id_overrides_config()
    {
        var steam = Answer(
            """{"response":{"params":{"result":"OK","steamid":"SID","ownersteamid":"SID"}}}""".Replace("SID", SteamId));
        using var service = Service(steam);

        var r = await service.AuthenticateAsync("ab12", appId: "730");

        Assert.Equal("730", r.Value!.AppId);
        Assert.False(r.Value.IsFamilyShared);
        Assert.Contains("appid=730", steam.Requests.Single().Query);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-hex&key=evil")]
    public async Task Malformed_ticket_is_a_validation_error_and_nothing_is_sent(string ticket)
    {
        var steam = Answer("{}");
        using var service = Service(steam);

        var r = await service.AuthenticateAsync(ticket);

        Assert.IsType<ValidationError>(r.Error);
        Assert.Equal("social.steam.invalid_ticket", r.Error!.Code);
        Assert.Empty(steam.Requests);
    }

    [Fact]
    public async Task Missing_app_id_is_a_validation_error()
    {
        using var service = Service(Answer("{}"), appId: "");
        var r = await service.AuthenticateAsync("ab12");
        Assert.IsType<ValidationError>(r.Error);
        Assert.Equal("social.steam.no_appid", r.Error!.Code);
    }

    [Fact]
    public async Task Steam_api_error_is_unauthorized_with_the_steam_error_code()
    {
        using var service = Service(Answer("""{"response":{"error":{"errorcode":101,"errordesc":"Invalid ticket"}}}"""));
        var r = await service.AuthenticateAsync("ab12");
        Assert.IsType<UnauthorizedError>(r.Error);
        Assert.Equal("social.steam.auth_failed", r.Error!.Code);
        Assert.Equal("101", r.Error.Details);
    }

    [Fact]
    public async Task Result_not_ok_is_unauthorized()
    {
        using var service = Service(Answer("""{"response":{"params":{"result":"Invalid"}}}"""));
        var r = await service.AuthenticateAsync("ab12");
        Assert.IsType<UnauthorizedError>(r.Error);
        Assert.Equal("social.steam.auth_failed", r.Error!.Code);
    }

    [Fact]
    public async Task Server_error_is_unavailable()
    {
        using var service = Service(Answer("", HttpStatusCode.BadGateway));
        var r = await service.AuthenticateAsync("ab12");
        Assert.IsType<UnavailableError>(r.Error);
        Assert.Equal("social.steam.network_error", r.Error!.Code);
    }

    [Fact]
    public async Task Client_error_such_as_a_bad_api_key_is_internal()
    {
        using var service = Service(Answer("", HttpStatusCode.Forbidden));
        var r = await service.AuthenticateAsync("ab12");
        Assert.IsType<InternalError>(r.Error);
        Assert.Equal("social.steam.request_rejected", r.Error!.Code);
        Assert.Equal("403", r.Error.Details);
    }

    [Fact]
    public async Task Unreadable_answer_is_internal_parse_error()
    {
        using var service = Service(Answer("<html>not json</html>"));
        var r = await service.AuthenticateAsync("ab12");
        Assert.IsType<InternalError>(r.Error);
        Assert.Equal("social.steam.parse_error", r.Error!.Code);
    }

    [Fact]
    public async Task Timeout_is_a_timeout_error()
    {
        using var service = Service(new FakeSteam(_ => throw new TaskCanceledException("timed out")));
        var r = await service.AuthenticateAsync("ab12");
        Assert.IsType<TimeoutError>(r.Error);
        Assert.Equal("social.steam.timeout", r.Error!.Code);
    }
}
