using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CL.GameNetQuery.Models;
using CodeLogic.Core.Results;

namespace CL.GameNetQuery.Valve;

/// <summary>
/// UDP-based query for Valve Source Engine servers.
/// Implements A2S_INFO and A2S_PLAYER protocols — no RCON password needed.
/// </summary>
public static class ValveUdpQuery
{
    private const int DefaultTimeout = 3000;

    /// <summary>
    /// Queries a Source Engine server with A2S_INFO and, when <paramref name="includePlayers"/> is set,
    /// A2S_PLAYER, and reports how long it took.
    /// </summary>
    /// <param name="ip">The server IPv4 or IPv6 address.</param>
    /// <param name="port">The server query port.</param>
    /// <param name="includePlayers">Also fetch the player list (one more round trip). Default: <c>true</c>.</param>
    /// <param name="timeoutMs">Timeout per request in milliseconds. Must be positive.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>
    /// A <see cref="Result{T}"/> containing the <see cref="ServerQuery"/> on success. Failures:
    /// <see cref="ValidationError"/> <c>gamenetquery.invalid_address</c> (not an IP address) or
    /// <c>gamenetquery.invalid_timeout</c>;
    /// <see cref="TimeoutError"/> <c>gamenetquery.timeout</c> (no answer within <paramref name="timeoutMs"/>);
    /// <see cref="UnavailableError"/> <c>gamenetquery.unreachable</c> (socket error, e.g. ICMP port unreachable);
    /// <see cref="InternalError"/> <c>gamenetquery.malformed_response</c> (answer could not be parsed) or
    /// <c>gamenetquery.query_failed</c> (anything else).
    /// </returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async Task<Result<ServerQuery>> QueryAsync(
        string ip, ushort port, bool includePlayers = true, int timeoutMs = DefaultTimeout,
        CancellationToken cancellationToken = default)
    {
        if (!IPAddress.TryParse(ip, out var address))
            return Error.Validation("gamenetquery.invalid_address", $"'{ip}' is not an IP address");
        if (timeoutMs <= 0)
            return Error.Validation("gamenetquery.invalid_timeout", $"Timeout must be positive, was {timeoutMs} ms");

        var endpoint = new IPEndPoint(address, port);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var info = await QueryInfoCoreAsync(endpoint, ip, port, timeoutMs, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<PlayerInfo> players = includePlayers
                ? await QueryPlayersCoreAsync(endpoint, timeoutMs, cancellationToken).ConfigureAwait(false)
                : [];
            return new ServerQuery(info, players, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error.Timeout("gamenetquery.timeout", $"No answer from {endpoint} within {timeoutMs} ms");
        }
        catch (SocketException ex)
        {
            return Error.Unavailable("gamenetquery.unreachable", $"{endpoint} is unreachable: {ex.Message}", ex.SocketErrorCode.ToString());
        }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException)
        {
            return Error.Internal("gamenetquery.malformed_response", $"Unreadable answer from {endpoint}: {ex.Message}", ex.GetType().Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error.FromException(ex, "gamenetquery.query_failed");
        }
    }

    /// <summary>
    /// Sends an A2S_INFO query to get basic server info.
    /// </summary>
    /// <param name="ip">The server IP address.</param>
    /// <param name="port">The server port number.</param>
    /// <param name="timeoutMs">Query timeout in milliseconds.</param>
    /// <returns>The server info, or <c>null</c> if the query failed. Use <see cref="QueryAsync"/> to learn why.</returns>
    public static async Task<ServerInfo?> GetServerInfoAsync(string ip, ushort port, int timeoutMs = DefaultTimeout)
    {
        try
        {
            var endpoint = new IPEndPoint(IPAddress.Parse(ip), port);
            return await QueryInfoCoreAsync(endpoint, ip, port, timeoutMs, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Sends an A2S_PLAYER query to get the player list with scores and durations.
    /// </summary>
    /// <param name="ip">The server IP address.</param>
    /// <param name="port">The server port number.</param>
    /// <param name="timeoutMs">Query timeout in milliseconds.</param>
    /// <returns>A list of player information entries; empty if the query failed.</returns>
    public static async Task<List<PlayerInfo>> GetPlayerListAsync(string ip, ushort port, int timeoutMs = DefaultTimeout)
    {
        try
        {
            var endpoint = new IPEndPoint(IPAddress.Parse(ip), port);
            return await QueryPlayersCoreAsync(endpoint, timeoutMs, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            return [];
        }
    }

    private static async Task<ServerInfo> QueryInfoCoreAsync(IPEndPoint endpoint, string ip, int port, int timeoutMs, CancellationToken ct)
    {
        using var client = new UdpClient(endpoint.AddressFamily) { Client = { ReceiveTimeout = timeoutMs } };

        var request = BuildInfoRequest();
        await client.SendAsync(request, request.Length, endpoint).ConfigureAwait(false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        var result = await client.ReceiveAsync(cts.Token).ConfigureAwait(false);

        // Since 2020 servers may answer A2S_INFO with S2C_CHALLENGE (0x41); repeat the request with it.
        if (result.Buffer.Length >= 9 && result.Buffer[4] == 0x41)
        {
            request = [.. request, .. result.Buffer.AsSpan(5, 4)];
            await client.SendAsync(request, request.Length, endpoint).ConfigureAwait(false);
            result = await client.ReceiveAsync(cts.Token).ConfigureAwait(false);
        }

        ExpectHeader(result.Buffer, 0x49, "A2S_INFO");
        return ParseInfoResponse(result.Buffer, ip, port);
    }

    private static async Task<List<PlayerInfo>> QueryPlayersCoreAsync(IPEndPoint endpoint, int timeoutMs, CancellationToken ct)
    {
        using var client = new UdpClient(endpoint.AddressFamily) { Client = { ReceiveTimeout = timeoutMs } };

        var challengeRequest = BuildPlayerRequest(-1);
        await client.SendAsync(challengeRequest, challengeRequest.Length, endpoint).ConfigureAwait(false);

        using var cts1 = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts1.CancelAfter(timeoutMs);
        var challengeResp = await client.ReceiveAsync(cts1.Token).ConfigureAwait(false);
        ExpectHeader(challengeResp.Buffer, 0x41, "A2S_PLAYER challenge");
        var challengeNumber = BitConverter.ToInt32(challengeResp.Buffer, 5);

        var playerRequest = BuildPlayerRequest(challengeNumber);
        await client.SendAsync(playerRequest, playerRequest.Length, endpoint).ConfigureAwait(false);

        using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts2.CancelAfter(timeoutMs);
        var playerResp = await client.ReceiveAsync(cts2.Token).ConfigureAwait(false);
        ExpectHeader(playerResp.Buffer, 0x44, "A2S_PLAYER");
        return ParsePlayerResponse(playerResp.Buffer);
    }

    private static void ExpectHeader(byte[] data, byte type, string what)
    {
        if (data.Length < 6 || data[0] != 0xFF || data[1] != 0xFF || data[2] != 0xFF || data[3] != 0xFF || data[4] != type)
            throw new InvalidDataException($"Not an {what} response (expected type 0x{type:X2})");
    }

    private static byte[] BuildInfoRequest()
    {
        var payload = new List<byte>();
        payload.AddRange([0xFF, 0xFF, 0xFF, 0xFF]);
        payload.Add(0x54); // A2S_INFO
        payload.AddRange(Encoding.UTF8.GetBytes("Source Engine Query\0"));
        return payload.ToArray();
    }

    private static byte[] BuildPlayerRequest(int challenge)
    {
        var payload = new List<byte>();
        payload.AddRange([0xFF, 0xFF, 0xFF, 0xFF]);
        payload.Add(0x55); // A2S_PLAYER
        payload.AddRange(BitConverter.GetBytes(challenge));
        return payload.ToArray();
    }

    private static ServerInfo ParseInfoResponse(byte[] data, string ip, int port)
    {
        var index = 6; // Skip header (4) + type (1) + protocol (1)
        var name = ReadNullTerminatedString(data, ref index);
        var map = ReadNullTerminatedString(data, ref index);
        var folder = ReadNullTerminatedString(data, ref index);
        var game = ReadNullTerminatedString(data, ref index);

        var steamAppId = data.Length > index + 1 ? BitConverter.ToInt16(data, index) : (short)0;
        index += 2;

        var playerCount = data.Length > index ? data[index++] : 0;
        var maxPlayers = data.Length > index ? data[index++] : 0;
        var botCount = data.Length > index ? data[index++] : 0;

        return new ServerInfo
        {
            Hostname = name,
            Map = map,
            PlayerCount = playerCount,
            MaxPlayers = maxPlayers,
            BotCount = botCount,
            Ip = ip,
            Port = port,
            IsOnline = true,
            QueriedUtc = DateTime.UtcNow
        };
    }

    private static List<PlayerInfo> ParsePlayerResponse(byte[] data)
    {
        var players = new List<PlayerInfo>();
        var index = 5; // Skip header + type
        var playerCount = data[index++];

        for (var i = 0; i < playerCount && index < data.Length; i++)
        {
            index++; // Skip player index byte
            var name = ReadNullTerminatedString(data, ref index);

            var score = index + 4 <= data.Length ? BitConverter.ToInt32(data, index) : 0;
            index += 4;

            var duration = index + 4 <= data.Length ? BitConverter.ToSingle(data, index) : 0f;
            index += 4;

            players.Add(new PlayerInfo
            {
                Name = name,
                Score = score,
                Duration = duration
            });
        }

        return players;
    }

    private static string ReadNullTerminatedString(byte[] data, ref int index)
    {
        var start = index;
        while (index < data.Length && data[index] != 0x00)
            index++;

        var result = Encoding.UTF8.GetString(data, start, index - start);
        index++; // Skip null byte
        return result;
    }
}
