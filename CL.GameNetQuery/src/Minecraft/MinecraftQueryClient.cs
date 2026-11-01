using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace CL.GameNetQuery.Minecraft;

/// <summary>
/// UDP query client for Minecraft servers.
/// Implements the Minecraft GameSpy4 query protocol (handshake, then a full-stat request).
/// </summary>
public static partial class MinecraftQueryClient
{
    private const int TimeoutMs = 3000;
    private const int BufferSize = 8192;
    private const byte TypeHandshake = 0x09;
    private const byte TypeStat = 0x00;

    /// <summary>Fixed padding after the type and session id of a full-stat response: <c>splitnum\0\x80\0</c>.</summary>
    private const int FullStatPadding = 11;

    private static ReadOnlySpan<byte> SplitNumPadding => [(byte)'s', (byte)'p', (byte)'l', (byte)'i', (byte)'t', (byte)'n', (byte)'u', (byte)'m', 0x00, 0x80, 0x00];

    /// <summary>
    /// Queries a Minecraft server and returns the parsed key-value response.
    /// </summary>
    /// <param name="ip">The server IP address as a string.</param>
    /// <param name="queryPort">The query port number.</param>
    /// <returns>The parsed server response as <c>key: value</c> lines; empty on failure or when <paramref name="ip"/> is not an IP address.</returns>
    public static string QueryServer(string ip, int queryPort) =>
        IPAddress.TryParse(ip, out var address) ? QueryServer(address, queryPort) : string.Empty;

    /// <summary>
    /// Queries a Minecraft server with a full-stat request and returns the parsed key-value response.
    /// </summary>
    /// <param name="serverIp">The server IP address.</param>
    /// <param name="queryPort">The query port number.</param>
    /// <returns>
    /// One <c>key: value</c> line per server field (<c>hostname</c>, <c>gametype</c>, <c>game_id</c>,
    /// <c>version</c>, <c>plugins</c>, <c>map</c>, <c>numplayers</c>, <c>maxplayers</c>, <c>hostport</c>,
    /// <c>hostip</c>), plus <c>players</c> with the online player names joined by <c>", "</c>. Empty on failure.
    /// </returns>
    public static string QueryServer(IPAddress serverIp, int queryPort)
    {
        try
        {
            var endpoint = new IPEndPoint(serverIp, queryPort);
            // A connected socket only accepts datagrams from the server; the session id is checked as well.
            using var udpClient = new UdpClient(serverIp.AddressFamily);
            udpClient.Connect(endpoint);
            udpClient.Client.ReceiveTimeout = TimeoutMs;
            udpClient.Client.ReceiveBufferSize = BufferSize;
            var deadline = Stopwatch.GetTimestamp() + TimeoutMs * Stopwatch.Frequency / 1000;

            var sessionId = Random.Shared.Next() & 0x0F0F0F0F;     // the protocol only keeps the low nibble of each byte

            var handshake = CreateHandshakeRequest(sessionId);
            udpClient.Send(handshake, handshake.Length);
            var challengeResponse = Receive(udpClient, endpoint, TypeHandshake, sessionId, deadline);
            if (challengeResponse is null || !TryParseChallengeToken(challengeResponse, out var challengeToken))
                return string.Empty;

            var queryRequest = CreateFullStatRequest(sessionId, challengeToken);
            udpClient.Send(queryRequest, queryRequest.Length);
            var serverResponse = Receive(udpClient, endpoint, TypeStat, sessionId, deadline);
            if (serverResponse is null || ParseFullStat(serverResponse) is not { } stat)
                return string.Empty;

            return FormatResponse(stat.Values, stat.Players);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Gets a specific value from a parsed query response.
    /// </summary>
    /// <param name="response">The parsed query response string.</param>
    /// <param name="key">The key to look up.</param>
    /// <returns>The value associated with the key, or empty string if not found.</returns>
    public static string GetStatusValue(string response, string key)
    {
        var cleanResponse = RemoveMinecraftFormatting(response);
        var lines = cleanResponse.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            var parts = line.Split(':', 2);
            if (parts.Length == 2)
                dict[parts[0].Trim()] = parts[1].Trim();
        }

        return dict.TryGetValue(key, out var value) ? value : string.Empty;
    }

    /// <summary>Waits for the next datagram from the server of the given type carrying our session id.</summary>
    private static byte[]? Receive(UdpClient client, IPEndPoint endpoint, byte type, int sessionId, long deadline)
    {
        while (Stopwatch.GetTimestamp() < deadline)
        {
            var from = new IPEndPoint(IPAddress.Any, 0);
            var data = client.Receive(ref from);
            if (from.Port == endpoint.Port && from.Address.Equals(endpoint.Address)
                && data.Length >= 5 && data[0] == type && BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(1)) == sessionId)
                return data;
        }
        return null;
    }

    internal static byte[] CreateHandshakeRequest(int sessionId)
    {
        var request = new byte[7];
        request[0] = 0xFE;
        request[1] = 0xFD;
        request[2] = TypeHandshake;
        BinaryPrimitives.WriteInt32BigEndian(request.AsSpan(3), sessionId);
        return request;
    }

    /// <summary>The challenge token is the decimal text after the type and session id, NUL-terminated.</summary>
    internal static bool TryParseChallengeToken(byte[] response, out int token)
    {
        token = 0;
        if (response.Length < 6)
            return false;
        var text = Encoding.ASCII.GetString(response, 5, response.Length - 5).TrimEnd('\0');
        return int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out token);
    }

    /// <summary>
    /// A full-stat request: magic, type 0, session id, challenge token, and four bytes of padding. Without the
    /// padding the server answers with the basic stat (MOTD, game type, map, counts, port, IP as bare values),
    /// which has no keys.
    /// </summary>
    internal static byte[] CreateFullStatRequest(int sessionId, int challengeToken)
    {
        var request = new byte[15];
        request[0] = 0xFE;
        request[1] = 0xFD;
        request[2] = TypeStat;
        BinaryPrimitives.WriteInt32BigEndian(request.AsSpan(3), sessionId);
        BinaryPrimitives.WriteInt32BigEndian(request.AsSpan(7), challengeToken);
        return request;                                          // bytes 11..14 stay zero: the full-stat padding
    }

    /// <summary>
    /// Parses a full-stat response: type, session id, 11 bytes of padding, NUL-separated key/value pairs ended
    /// by an empty key, <c>\x01player_\0\0</c>, then NUL-terminated player names ended by an empty name.
    /// </summary>
    /// <returns>The fields and player names, or <see langword="null"/> when the packet is not a full-stat response.</returns>
    internal static (IReadOnlyDictionary<string, string> Values, IReadOnlyList<string> Players)? ParseFullStat(byte[] response)
    {
        var index = 5 + FullStatPadding;
        if (response.Length < index || response[0] != TypeStat
            || !response.AsSpan(5, FullStatPadding).SequenceEqual(SplitNumPadding))
            return null;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            if (!TryReadString(response, ref index, out var key))
                return null;
            if (key.Length == 0)
                break;
            if (!TryReadString(response, ref index, out var value))
                return null;
            values[key] = value;
        }

        var players = new List<string>();
        // "\x01player_\0\0" introduces the player section; older servers may end the packet before it.
        if (index < response.Length && TryReadString(response, ref index, out var marker) && marker == "\x01player_")
        {
            index++;                                             // the second NUL after the marker
            while (index < response.Length && TryReadString(response, ref index, out var name) && name.Length > 0)
                players.Add(name);
        }

        return (values, players);
    }

    private static bool TryReadString(byte[] data, ref int index, out string value)
    {
        value = string.Empty;
        if (index >= data.Length)
            return false;
        var end = Array.IndexOf(data, (byte)0, index);
        if (end < 0)
            return false;
        value = Encoding.UTF8.GetString(data, index, end - index);
        index = end + 1;
        return true;
    }

    private static string FormatResponse(IReadOnlyDictionary<string, string> values, IReadOnlyList<string> players)
    {
        var lines = values.Select(kv => $"{Clean(kv.Key)}: {Clean(kv.Value)}").ToList();
        if (!values.ContainsKey("players"))
            lines.Add($"players: {string.Join(", ", players.Select(Clean))}");
        return string.Join(Environment.NewLine, lines);

        // One field per line: drop formatting codes and line breaks inside values (an MOTD may contain them).
        static string Clean(string s) => RemoveMinecraftFormatting(s).Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    private static string RemoveMinecraftFormatting(string input) => FormattingCode().Replace(input, string.Empty);

    [GeneratedRegex("[§&][0-9a-fk-or]", RegexOptions.IgnoreCase)]
    private static partial Regex FormattingCode();
}
