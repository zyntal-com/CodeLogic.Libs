using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CL.SocialConnect.Events;
using CL.SocialConnect.Models;
using CL.SocialConnect.Models.Discord;
using CodeLogic.Core.Events;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;

namespace CL.SocialConnect.Services.Discord;

/// <summary>
/// Sends messages and embeds to Discord channels via the Webhook API.
/// </summary>
public sealed class DiscordWebhookService : IDisposable
{
    private readonly Models.DiscordConfig _config;
    private readonly ILogger? _logger;
    private readonly IEventBus? _events;
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Initializes a new instance of <see cref="DiscordWebhookService"/>.
    /// </summary>
    /// <param name="config">Discord configuration (webhook URL, timeout, defaults).</param>
    /// <param name="logger">Optional scoped logger.</param>
    /// <param name="events">Optional event bus for publishing <see cref="WebhookSentEvent"/>.</param>
    public DiscordWebhookService(Models.DiscordConfig config, ILogger? logger = null, IEventBus? events = null)
    {
        _config = config;
        _logger = logger;
        _events = events;
        // No redirects: a webhook URL is validated before the request, and a redirect would take the
        // request (and its payload) somewhere that was never checked.
        _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds > 0 ? config.TimeoutSeconds : 10)
        };
    }

    // ── URL validation ────────────────────────────────────────────────────────

    private static readonly HashSet<string> WebhookHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "discord.com", "ptb.discord.com", "canary.discord.com",
        "discordapp.com", "ptb.discordapp.com", "canary.discordapp.com"
    };

    /// <summary>
    /// Whether <paramref name="url"/> is a Discord webhook URL this service will post to: <c>https</c> on the
    /// default port, no user info or fragment, host <c>discord.com</c> or <c>discordapp.com</c> (or their
    /// <c>ptb.</c>/<c>canary.</c> subdomains), and path <c>/api/webhooks/{id}/{token}</c> (optionally
    /// <c>/api/v{n}/webhooks/…</c>). A query string such as <c>?wait=true</c> or <c>?thread_id=…</c> is allowed.
    /// Anything else is refused so a caller-supplied URL cannot make the server send requests elsewhere (SSRF).
    /// </summary>
    /// <param name="url">The candidate webhook URL.</param>
    public static bool IsValidWebhookUrl(string? url) => TryParseWebhookUrl(url, out _, out _);

    private static bool TryParseWebhookUrl(string? url, out Uri uri, out string webhookId)
    {
        uri = null!;
        webhookId = string.Empty;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var parsed))
            return false;
        if (parsed.Scheme != Uri.UriSchemeHttps || !parsed.IsDefaultPort || parsed.UserInfo.Length > 0
            || parsed.Fragment.Length > 0 || parsed.HostNameType != UriHostNameType.Dns || !WebhookHosts.Contains(parsed.Host))
            return false;

        // Segments: "/", "api/", ["v10/",] "webhooks/", "{id}/", "{token}"
        var segments = parsed.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var i = 0;
        if (segments.Length < 4 || segments[i++] != "api") return false;
        if (segments[i].Length > 1 && segments[i][0] == 'v' && segments[i].AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0) i++;
        if (segments.Length != i + 3 || segments[i] != "webhooks") return false;

        var id = segments[i + 1];
        var token = segments[i + 2];
        if (id.Length is 0 or > 20 || id.AsSpan().IndexOfAnyExceptInRange('0', '9') >= 0) return false;
        foreach (var c in token)
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))
                return false;
        if (token.Length is 0 or > 200) return false;

        uri = parsed;
        webhookId = id;
        return true;
    }

    /// <summary>The webhook URL with its secret token replaced by <c>***</c>, safe to log or publish.</summary>
    /// <param name="url">A webhook URL.</param>
    public static string RedactWebhookUrl(string url)
    {
        if (TryParseWebhookUrl(url, out var uri, out var id))
        {
            var path = uri.AbsolutePath;
            var idAt = path.LastIndexOf("/" + id + "/", StringComparison.Ordinal);
            return $"{uri.Scheme}://{uri.Host}{path[..(idAt + id.Length + 2)]}***";
        }
        return "***";
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Sends a plain text message to a Discord webhook.
    /// </summary>
    /// <param name="content">The text content (max 2000 characters).</param>
    /// <param name="webhookUrl">
    /// Webhook URL to post to. Falls back to <c>config.DefaultWebhookUrl</c> when not specified.
    /// </param>
    /// <returns>A <see cref="Result"/> indicating whether the message was delivered successfully.</returns>
    public Task<Result> SendMessageAsync(string content, string? webhookUrl = null)
    {
        var message = new DiscordWebhookMessage
        {
            Content = content,
            Username = string.IsNullOrWhiteSpace(_config.DefaultUsername) ? null : _config.DefaultUsername,
            AvatarUrl = string.IsNullOrWhiteSpace(_config.DefaultAvatarUrl) ? null : _config.DefaultAvatarUrl
        };
        return SendAsync(message, webhookUrl);
    }

    /// <summary>
    /// Sends a message with one or more rich embeds to a Discord webhook.
    /// </summary>
    /// <param name="embeds">Embed objects to attach (max 10).</param>
    /// <param name="content">Optional plain text content alongside the embeds.</param>
    /// <param name="webhookUrl">
    /// Webhook URL to post to. Falls back to <c>config.DefaultWebhookUrl</c> when not specified.
    /// </param>
    /// <returns>A <see cref="Result"/> indicating whether the message was delivered successfully.</returns>
    public Task<Result> SendEmbedAsync(IEnumerable<DiscordEmbed> embeds, string? content = null, string? webhookUrl = null)
    {
        var message = new DiscordWebhookMessage
        {
            Content = content,
            Embeds = embeds.ToList(),
            Username = string.IsNullOrWhiteSpace(_config.DefaultUsername) ? null : _config.DefaultUsername,
            AvatarUrl = string.IsNullOrWhiteSpace(_config.DefaultAvatarUrl) ? null : _config.DefaultAvatarUrl
        };
        return SendAsync(message, webhookUrl);
    }

    /// <summary>
    /// Sends a fully constructed <see cref="DiscordWebhookMessage"/> to a Discord webhook.
    /// </summary>
    /// <param name="message">The message payload to send.</param>
    /// <param name="webhookUrl">
    /// Webhook URL to post to. Falls back to <c>config.DefaultWebhookUrl</c> when not specified.
    /// </param>
    /// <returns>A <see cref="Result"/> indicating whether the message was delivered successfully.</returns>
    public async Task<Result> SendAsync(DiscordWebhookMessage message, string? webhookUrl = null)
    {
        var url = ResolveUrl(webhookUrl);
        if (string.IsNullOrWhiteSpace(url))
        {
            const string msg = "No webhook URL provided and no default configured";
            _logger?.Error(msg);
            return Result.Failure(Error.Validation("social.discord.no_url", msg));
        }

        if (!TryParseWebhookUrl(url, out var webhookUri, out var webhookId))
        {
            const string msg = "The webhook URL is not a Discord webhook (https://discord.com/api/webhooks/{id}/{token})";
            _logger?.Warning($"{msg}: refused {RedactWebhookUrl(url)}");
            return Result.Failure(Error.Validation("social.discord.invalid_url", msg));
        }

        // Enforce Discord's hard limits up front so over-limit payloads fail with a clear
        // message instead of a wasted round-trip and an opaque HTTP 400.
        if (message.Content is { Length: > 2000 })
            return Result.Failure(Error.Validation(
                "social.discord.content_too_long",
                $"Message content is {message.Content.Length} characters; Discord allows at most 2000."));
        if (message.Embeds is { Count: > 10 })
            return Result.Failure(Error.Validation(
                "social.discord.too_many_embeds",
                $"Message has {message.Embeds.Count} embeds; Discord allows at most 10."));

        var sentAt = DateTime.UtcNow;

        try
        {
            var json = JsonSerializer.Serialize(message, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            _logger?.Debug($"Sending webhook to {RedactWebhookUrl(url)}");

            var response = await _http.PostAsync(webhookUri, content).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                _logger?.Debug($"Webhook delivered successfully ({(int)response.StatusCode})");
                PublishWebhookEvent(url, webhookId, true, sentAt, (int)response.StatusCode);
                return Result.Success();
            }

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var errMsg = $"Webhook delivery failed: HTTP {(int)response.StatusCode}. Body: {body}";
            _logger?.Warning(errMsg);
            PublishWebhookEvent(url, webhookId, false, sentAt, (int)response.StatusCode, errMsg);

            return Result.Failure(
                Error.Internal(
                    "social.discord.delivery_failed",
                    $"[{SocialError.WebhookDeliveryFailed}] HTTP {(int)response.StatusCode}",
                    body
                )
            );
        }
        catch (TaskCanceledException ex)
        {
            var errMsg = $"[{SocialError.Timeout}] Webhook request timed out: {ex.Message}";
            _logger?.Error(errMsg, ex);
            PublishWebhookEvent(url, webhookId, false, sentAt, null, errMsg);
            return Result.Failure(Error.Timeout("social.discord.timeout", errMsg));
        }
        catch (HttpRequestException ex)
        {
            var errMsg = $"[{SocialError.NetworkError}] Network error sending webhook: {ex.Message}";
            _logger?.Error(errMsg, ex);
            PublishWebhookEvent(url, webhookId, false, sentAt, null, errMsg);
            return Result.Failure(Error.Unavailable("social.discord.network_error", errMsg));
        }
        catch (Exception ex)
        {
            var errMsg = $"[{SocialError.NetworkError}] Unexpected error sending webhook: {ex.Message}";
            _logger?.Error(errMsg, ex);
            PublishWebhookEvent(url, webhookId, false, sentAt, null, errMsg);
            return Result.Failure(Error.Internal("social.discord.error", errMsg, ex.GetType().Name));
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string ResolveUrl(string? webhookUrl) =>
        !string.IsNullOrWhiteSpace(webhookUrl) ? webhookUrl : _config.DefaultWebhookUrl;

    private void PublishWebhookEvent(string url, string webhookId, bool success, DateTime sentAt, int? statusCode, string? errorMessage = null)
    {
        // Never publish the token: subscribers get the redacted URL and the webhook id.
        _events?.Publish(new WebhookSentEvent(RedactWebhookUrl(url), success, sentAt, statusCode, errorMessage)
        {
            WebhookId = webhookId
        });
    }

    public void Dispose() => _http.Dispose();
}
