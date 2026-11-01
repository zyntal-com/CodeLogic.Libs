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
            return Error.Validation(GameNetQueryErrorCodes.InvalidAddress, $"'{ip}' is not an IP address");
        if (timeoutMs <= 0)
            return Error.Validation(GameNetQueryErrorCodes.InvalidTimeout, $"Timeout must be positive, was {timeoutMs} ms");

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
            return Error.Timeout(GameNetQueryErrorCodes.Timeout, $"No answer from {endpoint} within {timeoutMs} ms");
        }
        catch (SocketException ex)
        {
            return Error.Unavailable(GameNetQueryErrorCodes.Unreachable, $"{endpoint} is unreachable: {ex.Message}", ex.SocketErrorCode.ToString());
        }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException)
        {
            return Error.Internal(GameNetQueryErrorCodes.MalformedResponse, $"Unreadable answer from {endpoint}: {ex.Message}", ex.GetType().Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error.FromException(ex, GameNetQueryErrorCodes.QueryFailed);
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
        using var client = Connect(endpoint, timeoutMs);

        var request = BuildInfoRequest();
        await client.SendAsync(request, request.Length).ConfigureAwait(false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        var buffer = await ReceiveFromAsync(client, endpoint, cts.Token).ConfigureAwait(false);

        // Since 2020 servers may answer A2S_INFO with S2C_CHALLENGE (0x41); repeat the request with it.
        if (buffer.Length >= 9 && buffer[4] == 0x41)
        {
            request = [.. request, .. buffer.AsSpan(5, 4)];
            await client.SendAsync(request, request.Length).ConfigureAwait(false);
            buffer = await ReceiveFromAsync(client, endpoint, cts.Token).ConfigureAwait(false);
        }

        ExpectHeader(buffer, 0x49, "A2S_INFO");
        return ParseInfoResponse(buffer, ip, port);
    }

    private static async Task<List<PlayerInfo>> QueryPlayersCoreAsync(IPEndPoint endpoint, int timeoutMs, CancellationToken ct)
    {
        using var client = Connect(endpoint, timeoutMs);

        var challengeRequest = BuildPlayerRequest(-1);
        await client.SendAsync(challengeRequest, challengeRequest.Length).ConfigureAwait(false);

        using var cts1 = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts1.CancelAfter(timeoutMs);
        var challengeResp = await ReceiveFromAsync(client, endpoint, cts1.Token).ConfigureAwait(false);
        ExpectHeader(challengeResp, 0x41, "A2S_PLAYER challenge");
        if (challengeResp.Length < 9)
            throw new InvalidDataException("A2S_PLAYER challenge is too short");
        var challengeNumber = BitConverter.ToInt32(challengeResp, 5);

        var playerRequest = BuildPlayerRequest(challengeNumber);
        await client.SendAsync(playerRequest, playerRequest.Length).ConfigureAwait(false);

        using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts2.CancelAfter(timeoutMs);
        var playerResp = await ReceiveFromAsync(client, endpoint, cts2.Token).ConfigureAwait(false);
        ExpectHeader(playerResp, 0x44, "A2S_PLAYER");
        return ParsePlayerResponse(playerResp);
    }

    /// <summary>
    /// A UDP socket connected to <paramref name="endpoint"/>: the OS then drops datagrams from any other
    /// source, so a third party cannot answer the query in the server's place.
    /// </summary>
    private static UdpClient Connect(IPEndPoint endpoint, int timeoutMs)
    {
        var client = new UdpClient(endpoint.AddressFamily) { Client = { ReceiveTimeout = timeoutMs } };
        try
        {
            client.Connect(endpoint);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Receives the next datagram that came from <paramref name="endpoint"/>; anything else is discarded.</summary>
    private static async Task<byte[]> ReceiveFromAsync(UdpClient client, IPEndPoint endpoint, CancellationToken ct)
    {
        while (true)
        {
            var result = await client.ReceiveAsync(ct).ConfigureAwait(false);
            if (IsFrom(result.RemoteEndPoint, endpoint))
                return result.Buffer;
        }
    }

    /// <summary>Whether <paramref name="remote"/> is <paramref name="expected"/> (IPv4-mapped IPv6 compares as IPv4).</summary>
    internal static bool IsFrom(IPEndPoint remote, IPEndPoint expected) =>
        remote.Port == expected.Port && Normalize(remote.Address).Equals(Normalize(expected.Address));

    private static IPAddress Normalize(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;

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

        // Optional tail: server type, environment, visibility, VAC, [The Ship: 3 bytes], version, extra data flag.
        var version = string.Empty;
        IReadOnlyList<string> tags = [];
        index += 4;
        if (steamAppId == 2400)
            index += 3;
        if (index < data.Length)
        {
            version = ReadNullTerminatedString(data, ref index);
            if (index < data.Length)
            {
                var edf = data[index++];
                if ((edf & 0x80) != 0) index += 2;                   // game port
                if ((edf & 0x10) != 0) index += 8;                   // server SteamID
                if ((edf & 0x40) != 0)                               // SourceTV port and name
                {
                    index += 2;
                    if (index < data.Length) ReadNullTerminatedString(data, ref index);
                }
                if ((edf & 0x20) != 0 && index < data.Length)        // keywords (sv_tags)
                    tags = ReadNullTerminatedString(data, ref index)
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
        }

        return new ServerInfo
        {
            Hostname = name,
            Map = map,
            PlayerCount = playerCount,
            MaxPlayers = maxPlayers,
            BotCount = botCount,
            Version = version,
            Tags = tags,
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

    /// <summary>Reads a NUL-terminated UTF-8 string at <paramref name="index"/> and moves past the NUL.</summary>
    /// <exception cref="InvalidDataException">The string starts past the end of the packet or has no terminator.</exception>
    internal static string ReadNullTerminatedString(byte[] data, ref int index)
    {
        if (index < 0 || index >= data.Length)
            throw new InvalidDataException("A2S string starts past the end of the packet");
        var length = Array.IndexOf(data, (byte)0, index) - index;
        if (length < 0)
            throw new InvalidDataException("A2S string has no terminator");

        var result = Encoding.UTF8.GetString(data, index, length);
        index += length + 1;
        return result;
    }
}
