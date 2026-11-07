using System.Text.Json;
using System.Text.Json.Serialization;
using CL.SocialConnect.Events;
using CL.SocialConnect.Models;
using CL.SocialConnect.Models.Steam;
using CodeLogic.Core.Events;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;

namespace CL.SocialConnect.Services.Steam;

/// <summary>
/// Retrieves Steam player profiles, ban records, and game libraries via the Steam Web API.
/// Results are cached for a configurable TTL to reduce API call volume; each cache holds at most
/// <c>SteamConfig.MaxCacheEntries</c> entries and evicts the oldest when full.
/// </summary>
public sealed partial class SteamProfileService : IDisposable
{
    private readonly Models.SteamConfig _config;
    private readonly ILogger? _logger;
    private readonly IEventBus? _events;
    private readonly HttpClient _http;

    // Bounded, thread-safe caches: key → data, expiring after CacheTtlSeconds.
    private readonly BoundedCache<SteamPlayer> _playerCache;
    private readonly BoundedCache<SteamPlayerBans> _bansCache;
    private readonly BoundedCache<List<SteamGame>> _gamesCache;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Initializes a new instance of <see cref="SteamProfileService"/>.
    /// </summary>
    /// <param name="config">Steam configuration (API key, timeouts, cache TTL and size).</param>
    /// <param name="logger">Optional scoped logger.</param>
    /// <param name="events">Optional event bus for publishing profile fetch events.</param>
    /// <exception cref="ArgumentException"><c>config.ApiBaseUrl</c> is not an absolute https URL without user info.</exception>
    public SteamProfileService(Models.SteamConfig config, ILogger? logger = null, IEventBus? events = null)
        : this(config, new HttpClientHandler(), logger, events)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="SteamProfileService"/> that sends requests through
    /// <paramref name="handler"/> (tests, outbound proxies).
    /// </summary>
    /// <param name="config">Steam configuration (API key, timeouts, cache TTL and size).</param>
    /// <param name="handler">HTTP handler for the Steam Web API requests; disposed with the service.</param>
    /// <param name="logger">Optional scoped logger.</param>
    /// <param name="events">Optional event bus for publishing profile fetch events.</param>
    /// <exception cref="ArgumentException"><c>config.ApiBaseUrl</c> is not an absolute https URL without user info.</exception>
    public SteamProfileService(Models.SteamConfig config, HttpMessageHandler handler, ILogger? logger = null, IEventBus? events = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(handler);
        if (!config.TryGetApiBaseUri(out var baseAddress, out var urlError))
            throw new ArgumentException(urlError, nameof(config));

        _config = config;
        _logger = logger;
        _events = events;
        var ttl = TimeSpan.FromSeconds(config.CacheTtlSeconds > 0 ? config.CacheTtlSeconds : 300);
        var maxEntries = config.MaxCacheEntries > 0 ? config.MaxCacheEntries : 10_000;
        _playerCache = new BoundedCache<SteamPlayer>(ttl, maxEntries);
        _bansCache = new BoundedCache<SteamPlayerBans>(ttl, maxEntries);
        _gamesCache = new BoundedCache<List<SteamGame>>(ttl, maxEntries);
        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds > 0 ? config.TimeoutSeconds : 15),
            BaseAddress = baseAddress
        };
    }
}
