using System.Net;
using CL.Common.Parser;
using CL.Common.Web;
using CodeLogic.Core.Results;
using Xunit;

namespace Common.Tests;

/// <summary>HtmlHelper truncation limits, CronParser local times and HttpClientHelper error kinds.</summary>
public sealed class FollowupFixTests
{
    [Theory]
    [InlineData("<b>Hello world</b>", 8, "...", "Hello...")]
    [InlineData("<b>Hello world</b>", 2, "...", "He")]   // the suffix does not fit: plain cut
    [InlineData("<b>Hello world</b>", 0, "...", "")]
    [InlineData("<b>Hello world</b>", -1, "...", "")]
    [InlineData("<b>Hello</b>", 5, "...", "Hello")]
    [InlineData("<b>Hello world</b>", 5, "", "Hello")]
    public void TruncateText_never_throws_and_respects_the_limit(string html, int max, string suffix, string expected)
    {
        var text = HtmlHelper.TruncateText(html, max, suffix);
        Assert.Equal(expected, text);
        Assert.True(text.Length <= Math.Max(0, max));
    }

    [Fact]
    public void A_local_start_time_is_converted_to_utc()
    {
        var utcFrom = new DateTime(2026, 6, 1, 10, 7, 0, DateTimeKind.Utc);
        var localFrom = utcFrom.ToLocalTime();

        var fromUtc = CronParser.GetNextOccurrence("*/15 * * * *", utcFrom).ValueOrThrow();
        var fromLocal = CronParser.GetNextOccurrence("*/15 * * * *", localFrom).ValueOrThrow();

        Assert.Equal(new DateTime(2026, 6, 1, 10, 15, 0, DateTimeKind.Utc), fromUtc);
        Assert.Equal(fromUtc, fromLocal);
        Assert.Equal(DateTimeKind.Utc, fromLocal.Kind);
    }

    [Fact]
    public void An_unspecified_start_time_is_taken_as_utc()
    {
        var next = CronParser.GetNextOccurrence("0 * * * *", new DateTime(2026, 6, 1, 10, 7, 0, DateTimeKind.Unspecified)).ValueOrThrow();
        Assert.Equal(new DateTime(2026, 6, 1, 11, 0, 0, DateTimeKind.Utc), next);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, typeof(ValidationError))]
    [InlineData(HttpStatusCode.UnprocessableEntity, typeof(ValidationError))]
    [InlineData(HttpStatusCode.Unauthorized, typeof(UnauthorizedError))]
    [InlineData(HttpStatusCode.Forbidden, typeof(UnauthorizedError))]
    [InlineData(HttpStatusCode.NotFound, typeof(NotFoundError))]
    [InlineData(HttpStatusCode.Gone, typeof(NotFoundError))]
    [InlineData(HttpStatusCode.Conflict, typeof(ConflictError))]
    [InlineData(HttpStatusCode.PreconditionFailed, typeof(ConflictError))]
    [InlineData(HttpStatusCode.RequestTimeout, typeof(TimeoutError))]
    [InlineData(HttpStatusCode.GatewayTimeout, typeof(TimeoutError))]
    [InlineData(HttpStatusCode.TooManyRequests, typeof(UnavailableError))]
    [InlineData(HttpStatusCode.ServiceUnavailable, typeof(UnavailableError))]
    [InlineData(HttpStatusCode.BadGateway, typeof(UnavailableError))]
    [InlineData(HttpStatusCode.InternalServerError, typeof(InternalError))]
    [InlineData(HttpStatusCode.NotImplemented, typeof(InternalError))]
    public async Task Status_codes_map_to_error_kinds(HttpStatusCode status, Type kind)
    {
        using var client = new HttpClient(new Respond(_ => new HttpResponseMessage(status) { Content = new StringContent("nope") }));
        var result = await HttpClientHelper.GetStringAsync(client, "http://example.test/");

        var error = Assert.IsAssignableFrom<Error>(result.Error);
        Assert.IsType(kind, error);
        Assert.Equal(HttpClientHelper.StatusErrorCode((int)status), error.Code);
        Assert.Equal($"HTTP {(int)status}: nope", error.Message);
    }

    [Fact]
    public async Task Transport_failures_and_timeouts_have_their_own_kinds()
    {
        using var down = new HttpClient(new Respond(_ => throw new HttpRequestException("refused")));
        var unavailable = await HttpClientHelper.DeleteAsync(down, "http://example.test/");
        Assert.IsType<UnavailableError>(unavailable.Error);
        Assert.Equal(ErrorCode.ConnectionFailed, unavailable.Error!.Code);

        using var slow = new HttpClient(new Respond(_ => throw new TaskCanceledException("timed out")));
        var timeout = await HttpClientHelper.GetStringAsync(slow, "http://example.test/");
        Assert.IsType<TimeoutError>(timeout.Error);
        Assert.Equal(ErrorCode.ConnectionTimeout, timeout.Error!.Code);

        using var ok = new HttpClient(new Respond(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var invalid = await HttpClientHelper.GetStringAsync(ok, "not a url");
        Assert.IsType<ValidationError>(invalid.Error);
        Assert.Equal(ErrorCode.InvalidArgument, invalid.Error!.Code);
    }

    private sealed class Respond(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
