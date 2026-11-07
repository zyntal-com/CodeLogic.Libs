using System.Text.Json;
using System.Text.Json.Serialization;
using CL.SocialConnect.Events;
using CL.SocialConnect.Models;
using CL.SocialConnect.Models.Steam;
using CodeLogic.Core.Events;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;

namespace CL.SocialConnect.Services.Steam;

public sealed partial class SteamProfileService
{

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
}
