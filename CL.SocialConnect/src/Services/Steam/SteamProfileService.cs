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
public sealed class SteamProfileService : IDisposable
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

    // ── Player Summary ────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches the public profile summary for a Steam player by their 64-bit Steam ID.
    /// Results are cached according to <c>config.CacheTtlSeconds</c>.
    /// </summary>
    /// <param name="steamId">The 64-bit Steam ID string (17 digits, <c>7656119…</c>); anything else is a
    /// <c>social.steam.invalid_steamid</c> validation error and no request is made.</param>
    /// <param name="cancellationToken">Cancels the request (<c>social.steam.cancelled</c>).</param>
    /// <returns>A <see cref="Result{T}"/> containing the <see cref="SteamPlayer"/> profile on success.</returns>
    public async Task<Result<SteamPlayer>> GetPlayerAsync(string steamId, CancellationToken cancellationToken = default)
    {
        if (InvalidSteamId(steamId) is { } invalid)
            return Result<SteamPlayer>.Failure(invalid);

        // Check cache
        if (_playerCache.TryGet(steamId, out var cached))
        {
            _logger?.Debug($"Steam player cache hit: {steamId}");
            _events?.Publish(new SteamProfileFetchedEvent(steamId, DateTime.UtcNow, FromCache: true));
            return Result<SteamPlayer>.Success(cached);
        }

        try
        {
            var url = $"ISteamUser/GetPlayerSummaries/v0002/?key={Uri.EscapeDataString(_config.ApiKey)}&steamids={Uri.EscapeDataString(steamId)}";
            _logger?.Debug($"Fetching Steam player profile: {steamId}");

            var json = await _http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<SteamPlayerSummariesResponse>(json, JsonOptions);

            var player = response?.Response?.Players?.FirstOrDefault();
            if (player is null)
            {
                var errMsg = $"[{SocialError.UserNotFound}] No player found for SteamId: {steamId}";
                _logger?.Warning(errMsg);
                return Result<SteamPlayer>.Failure(Error.NotFound("social.steam.player_not_found", errMsg, steamId));
            }

            _playerCache.Set(steamId, player);
            _events?.Publish(new SteamProfileFetchedEvent(steamId, DateTime.UtcNow, FromCache: false));

            _logger?.Debug($"Steam player fetched: {player.PersonaName}");
            return Result<SteamPlayer>.Success(player);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result<SteamPlayer>.Failure(Cancelled($"Steam player request for {steamId}"));
        }
        catch (OperationCanceledException ex)
        {
            var errMsg = $"[{SocialError.Timeout}] Steam API request timed out for {steamId}: {ex.Message}";
            _logger?.Error(errMsg, ex);
            return Result<SteamPlayer>.Failure(Error.Timeout("social.steam.timeout", errMsg));
        }
        catch (HttpRequestException ex)
        {
            var errMsg = $"[{SocialError.NetworkError}] Network error fetching Steam player {steamId}: {ex.Message}";
            _logger?.Error(errMsg, ex);
            return Result<SteamPlayer>.Failure(Error.Unavailable("social.steam.network_error", errMsg));
        }
        catch (JsonException ex)
        {
            var errMsg = $"[{SocialError.SerializationError}] Failed to parse Steam API response for {steamId}: {ex.Message}";
            _logger?.Error(errMsg, ex);
            return Result<SteamPlayer>.Failure(Error.Internal("social.steam.parse_error", errMsg));
        }
        catch (Exception ex)
        {
            var errMsg = $"[{SocialError.NetworkError}] Unexpected error fetching Steam player {steamId}: {ex.Message}";
            _logger?.Error(errMsg, ex);
            return Result<SteamPlayer>.Failure(Error.Internal("social.steam.error", errMsg, ex.GetType().Name));
        }
    }

    // ── Player Bans ───────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches the VAC and game ban status for a Steam player.
    /// Results are cached according to <c>config.CacheTtlSeconds</c>.
    /// </summary>
    /// <param name="steamId">The 64-bit Steam ID string (17 digits, <c>7656119…</c>); anything else is a
    /// <c>social.steam.invalid_steamid</c> validation error and no request is made.</param>
    /// <param name="cancellationToken">Cancels the request (<c>social.steam.cancelled</c>).</param>
    /// <returns>A <see cref="Result{T}"/> containing the <see cref="SteamPlayerBans"/> record on success.</returns>
    public async Task<Result<SteamPlayerBans>> GetPlayerBansAsync(string steamId, CancellationToken cancellationToken = default)
    {
        if (InvalidSteamId(steamId) is { } invalid)
            return Result<SteamPlayerBans>.Failure(invalid);

        if (_bansCache.TryGet(steamId, out var cached))
        {
            _logger?.Debug($"Steam bans cache hit: {steamId}");
            return Result<SteamPlayerBans>.Success(cached);
        }

        try
        {
            var url = $"ISteamUser/GetPlayerBans/v1/?key={Uri.EscapeDataString(_config.ApiKey)}&steamids={Uri.EscapeDataString(steamId)}";
            var json = await _http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<SteamPlayerBansResponse>(json, JsonOptions);

            var bans = response?.Players?.FirstOrDefault();
            if (bans is null)
            {
                var errMsg = $"[{SocialError.UserNotFound}] No ban record found for SteamId: {steamId}";
                _logger?.Warning(errMsg);
                return Result<SteamPlayerBans>.Failure(Error.NotFound("social.steam.bans_not_found", errMsg, steamId));
            }

            _bansCache.Set(steamId, bans);
            return Result<SteamPlayerBans>.Success(bans);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result<SteamPlayerBans>.Failure(Cancelled($"Steam bans request for {steamId}"));
        }
        catch (OperationCanceledException ex)
        {
            var errMsg = $"[{SocialError.Timeout}] Steam bans request timed out for {steamId}: {ex.Message}";
            _logger?.Error(errMsg, ex);
            return Result<SteamPlayerBans>.Failure(Error.Timeout("social.steam.timeout", errMsg));
        }
        catch (HttpRequestException ex)
        {
            var errMsg = $"[{SocialError.NetworkError}] Network error fetching Steam bans for {steamId}: {ex.Message}";
            _logger?.Error(errMsg, ex);
            return Result<SteamPlayerBans>.Failure(Error.Unavailable("social.steam.network_error", errMsg));
        }
        catch (JsonException ex)
        {
            var errMsg = $"[{SocialError.SerializationError}] Failed to parse Steam bans response for {steamId}: {ex.Message}";
            _logger?.Error(errMsg, ex);
            return Result<SteamPlayerBans>.Failure(Error.Internal("social.steam.parse_error", errMsg));
        }
        catch (Exception ex)
        {
            var errMsg = $"[{SocialError.NetworkError}] Unexpected error fetching bans for {steamId}: {ex.Message}";
            _logger?.Error(errMsg, ex);
            return Result<SteamPlayerBans>.Failure(Error.Internal("social.steam.error", errMsg, ex.GetType().Name));
        }
    }

    // ── Owned Games ───────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches the list of games owned by a Steam player.
    /// Requires the player's games list to be set to public.
    /// Results are cached according to <c>config.CacheTtlSeconds</c>.
    /// </summary>
    /// <param name="steamId">The 64-bit Steam ID string (17 digits, <c>7656119…</c>); anything else is a
    /// <c>social.steam.invalid_steamid</c> validation error and no request is made.</param>
    /// <param name="includeAppInfo">When <c>true</c>, include game name and image data (more data transferred).</param>
    /// <param name="cancellationToken">Cancels the request (<c>social.steam.cancelled</c>).</param>
    /// <returns>A <see cref="Result{T}"/> containing the list of <see cref="SteamGame"/> objects on success.</returns>
    public async Task<Result<List<SteamGame>>> GetOwnedGamesAsync(string steamId, bool includeAppInfo = true, CancellationToken cancellationToken = default)
    {
        if (InvalidSteamId(steamId) is { } invalid)
            return Result<List<SteamGame>>.Failure(invalid);

        var cacheKey = $"{steamId}:{includeAppInfo}";
        if (_gamesCache.TryGet(cacheKey, out var cached))
        {
            _logger?.Debug($"Steam games cache hit: {steamId}");
            return Result<List<SteamGame>>.Success(cached);
        }

        try
        {
            var appInfoFlag = includeAppInfo ? 1 : 0;
            var url = $"IPlayerService/GetOwnedGames/v0001/?key={Uri.EscapeDataString(_config.ApiKey)}&steamid={Uri.EscapeDataString(steamId)}&include_appinfo={appInfoFlag}&include_played_free_games=1&format=json";
            var json = await _http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<SteamOwnedGamesResponse>(json, JsonOptions);

            var games = response?.Response?.Games ?? [];
            _gamesCache.Set(cacheKey, games);

            _logger?.Debug($"Steam games fetched for {steamId}: {games.Count} games");
            return Result<List<SteamGame>>.Success(games);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result<List<SteamGame>>.Failure(Cancelled($"Steam games request for {steamId}"));
        }
        catch (OperationCanceledException ex)
        {
            var errMsg = $"[{SocialError.Timeout}] Steam games request timed out for {steamId}: {ex.Message}";
            _logger?.Error(errMsg, ex);
            return Result<List<SteamGame>>.Failure(Error.Timeout("social.steam.timeout", errMsg));
        }
        catch (HttpRequestException ex)
        {
            var errMsg = $"[{SocialError.NetworkError}] Network error fetching Steam games for {steamId}: {ex.Message}";
            _logger?.Error(errMsg, ex);
            return Result<List<SteamGame>>.Failure(Error.Unavailable("social.steam.network_error", errMsg));
        }
        catch (JsonException ex)
        {
            var errMsg = $"[{SocialError.SerializationError}] Failed to parse Steam games response for {steamId}: {ex.Message}";
            _logger?.Error(errMsg, ex);
            return Result<List<SteamGame>>.Failure(Error.Internal("social.steam.parse_error", errMsg));
        }
        catch (Exception ex)
        {
            var errMsg = $"[{SocialError.NetworkError}] Unexpected error fetching games for {steamId}: {ex.Message}";
            _logger?.Error(errMsg, ex);
            return Result<List<SteamGame>>.Failure(Error.Internal("social.steam.error", errMsg, ex.GetType().Name));
        }
    }

    // ── Validation ────────────────────────────────────────────────────────────

    /// <summary>
    /// Refuses anything but a SteamID64 (<see cref="SteamIds.IsSteamId64"/>) before it reaches the query string,
    /// the cache or the log.
    /// </summary>
    private static Error? InvalidSteamId(string steamId)
    {
        if (string.IsNullOrWhiteSpace(steamId))
            return Error.Validation("social.steam.invalid_steamid", $"[{SocialError.UserNotFound}] SteamId cannot be empty");
        if (!SteamIds.IsSteamId64(steamId))
            return Error.Validation("social.steam.invalid_steamid",
                $"[{SocialError.UserNotFound}] SteamId must be a 17-digit SteamID64 (7656119…)");
        return null;
    }

    private Error Cancelled(string what)
    {
        var errMsg = $"{what} was cancelled";
        _logger?.Warning(errMsg);
        return Error.Unavailable("social.steam.cancelled", errMsg);
    }

    // ── Cache ─────────────────────────────────────────────────────────────────

    /// <summary>Clears all cached player data.</summary>
    public void ClearCache()
    {
        _playerCache.Clear();
        _bansCache.Clear();
        _gamesCache.Clear();
        _logger?.Debug("Steam profile cache cleared");
    }

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// A thread-safe cache with a TTL and an upper bound: an expired entry is dropped when read, and when the
    /// cache is full the oldest entries (by insertion time, which is also expiry order) make room for a new one.
    /// </summary>
    private sealed class BoundedCache<T>(TimeSpan ttl, int maxEntries)
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<string, (T data, DateTime cached)> _entries = new(StringComparer.Ordinal);
        // Insertion order; a record is stale when the dictionary holds a newer cached time for its key.
        private Queue<(string key, DateTime cached)> _order = new();

        public bool TryGet(string key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out T data)
        {
            lock (_lock)
            {
                if (_entries.TryGetValue(key, out var entry))
                {
                    if (DateTime.UtcNow - entry.cached <= ttl)
                    {
                        data = entry.data;
                        return true;
                    }
                    _entries.Remove(key);
                }
            }
            data = default;
            return false;
        }

        public void Set(string key, T data)
        {
            var now = DateTime.UtcNow;
            lock (_lock)
            {
                if (!_entries.ContainsKey(key))
                    EvictWhileFull();
                _entries[key] = (data, now);
                _order.Enqueue((key, now));
                // Refreshing the same keys leaves stale records in the queue; compact it before it outgrows the cache.
                if (_order.Count > 2 * maxEntries)
                    _order = new Queue<(string key, DateTime cached)>(_order.Where(IsCurrent));
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _entries.Clear();
                _order.Clear();
            }
        }

        /// <summary>Drops the oldest entries until there is room for one more. Every entry has a queue record, so this ends.</summary>
        private void EvictWhileFull()
        {
            while (_entries.Count >= maxEntries && _order.TryDequeue(out var oldest))
            {
                // A record superseded by a later Set of the same key (or dropped by TryGet) is skipped; the live record is further back.
                if (IsCurrent(oldest))
                    _entries.Remove(oldest.key);
            }
        }

        private bool IsCurrent((string key, DateTime cached) record) =>
            _entries.TryGetValue(record.key, out var current) && current.cached == record.cached;
    }

    // ── Internal response models ──────────────────────────────────────────────

    private class SteamPlayerSummariesResponse
    {
        [JsonPropertyName("response")]
        public SteamPlayerSummariesResult? Response { get; set; }
    }

    private class SteamPlayerSummariesResult
    {
        [JsonPropertyName("players")]
        public List<SteamPlayer>? Players { get; set; }
    }

    private class SteamPlayerBansResponse
    {
        [JsonPropertyName("players")]
        public List<SteamPlayerBans>? Players { get; set; }
    }

    private class SteamOwnedGamesResponse
    {
        [JsonPropertyName("response")]
        public SteamOwnedGamesResult? Response { get; set; }
    }

    private class SteamOwnedGamesResult
    {
        [JsonPropertyName("games")]
        public List<SteamGame>? Games { get; set; }
    }
}
