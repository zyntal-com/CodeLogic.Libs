using System.Net;
using CL.SocialConnect.Events;
using CL.SocialConnect.Models;
using CL.SocialConnect.Services.Discord;
using CodeLogic.Core.Events;
using CodeLogic.Core.Results;
using Xunit;

namespace SocialConnect.Tests;

/// <summary>
/// Discord webhook delivery, offline: Discord's answer comes from a fake handler.
/// </summary>
public sealed class DiscordWebhookTests
{
    private const string Token = "secret-token-XYZ";
    private const string WebhookUrl = "https://discord.com/api/webhooks/42/" + Token;

    private sealed class FakeDiscord(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return respond(request);
        }
    }

    private static FakeDiscord Answer(HttpStatusCode status, string body = "") =>
        new(_ => new HttpResponseMessage(status) { Content = new StringContent(body) });

    private static (DiscordWebhookService service, TestLogger logger, List<WebhookSentEvent> events) Service(HttpMessageHandler handler)
    {
        var logger = new TestLogger();
        var bus = new EventBus();
        var events = new List<WebhookSentEvent>();
        bus.Subscribe<WebhookSentEvent>(events.Add);
        var service = new DiscordWebhookService(new DiscordConfig { DefaultWebhookUrl = WebhookUrl, TimeoutSeconds = 5 }, handler, logger, bus);
        return (service, logger, events);
    }

    [Fact]
    public async Task Successful_send_posts_to_the_webhook_and_publishes_a_redacted_event()
    {
        var discord = Answer(HttpStatusCode.NoContent);
        var (service, logger, events) = Service(discord);
        using (service)
        {
            var result = await service.SendMessageAsync("hello");

            Assert.True(result.IsSuccess, result.Error?.Message);
            var request = Assert.Single(discord.Requests);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(WebhookUrl, request.RequestUri!.ToString());

            var sent = Assert.Single(events);
            Assert.True(sent.Success);
            Assert.Equal(204, sent.StatusCode);
            Assert.Equal("42", sent.WebhookId);
            Assert.Equal("https://discord.com/api/webhooks/42/***", sent.WebhookUrl);
            Assert.DoesNotContain(Token, sent.ToString());
            Assert.DoesNotContain(Token, logger.All);
            Assert.NotEmpty(logger.Lines);
        }
    }

    [Fact]
    public async Task Redirect_answers_are_not_followed_and_count_as_failed_delivery()
    {
        var discord = new FakeDiscord(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found) { Content = new StringContent("") };
            response.Headers.Location = new Uri("https://evil.example/collect");
            return response;
        });
        var (service, logger, events) = Service(discord);
        using (service)
        {
            var result = await service.SendMessageAsync("hello");

            Assert.True(result.IsFailure);
            Assert.Equal(SocialConnectErrorCodes.DiscordDeliveryFailed, result.Error!.Code);
            Assert.Equal("Webhook delivery failed: HTTP 302", result.Error.Message);
            Assert.Single(discord.Requests);
            Assert.All(discord.Requests, r => Assert.Equal("discord.com", r.RequestUri!.Host));
            var sent = Assert.Single(events);
            Assert.False(sent.Success);
            Assert.Equal(302, sent.StatusCode);
            Assert.DoesNotContain(Token, logger.All);
        }
    }

    [Fact]
    public void Default_handlers_have_automatic_redirects_switched_off()
    {
        var sockets = new SocketsHttpHandler { AllowAutoRedirect = true };
        using var service = new DiscordWebhookService(new DiscordConfig(), sockets);
        Assert.False(sockets.AllowAutoRedirect);

        var client = new HttpClientHandler { AllowAutoRedirect = true };
        using var service2 = new DiscordWebhookService(new DiscordConfig(), client);
        Assert.False(client.AllowAutoRedirect);
    }

    [Fact]
    public async Task Failed_delivery_event_and_log_carry_the_redacted_url_only()
    {
        var (service, logger, events) = Service(Answer(HttpStatusCode.BadRequest, """{"message":"bad"}"""));
        using (service)
        {
            var result = await service.SendMessageAsync("hello", "https://discord.com/api/webhooks/7/" + Token);

            Assert.Equal(SocialConnectErrorCodes.DiscordDeliveryFailed, result.Error!.Code);
            var sent = Assert.Single(events);
            Assert.Equal("https://discord.com/api/webhooks/7/***", sent.WebhookUrl);
            Assert.Equal("7", sent.WebhookId);
            Assert.DoesNotContain(Token, sent.ToString());
            Assert.DoesNotContain(Token, logger.All);
        }
    }

    [Fact]
    public async Task Cancelled_send_is_unavailable_with_the_cancelled_code()
    {
        using var cts = new CancellationTokenSource();
        var discord = new FakeDiscord(_ => { cts.Cancel(); throw new OperationCanceledException(cts.Token); });
        var (service, _, events) = Service(discord);
        using (service)
        {
            var result = await service.SendMessageAsync("hello", cancellationToken: cts.Token);

            Assert.IsType<UnavailableError>(result.Error);
            Assert.Equal(SocialConnectErrorCodes.DiscordCancelled, result.Error!.Code);
            Assert.False(Assert.Single(events).Success);
        }
    }

    [Fact]
    public async Task Request_timeout_stays_a_timeout_error()
    {
        var (service, _, _) = Service(new FakeDiscord(_ => throw new TaskCanceledException("timed out", new TimeoutException())));
        using (service)
        {
            var result = await service.SendMessageAsync("hello");
            Assert.IsType<TimeoutError>(result.Error);
            Assert.Equal(SocialConnectErrorCodes.DiscordTimeout, result.Error!.Code);
        }
    }
}
