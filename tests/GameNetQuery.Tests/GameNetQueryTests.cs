using System.Text;
using CL.GameNetQuery;
using CL.GameNetQuery.Minecraft;
using CL.GameNetQuery.Valve;
using CodeLogic.Framework.Libraries; // HealthStatusLevel
using Xunit;

namespace GameNetQuery.Tests;

// Offline unit tests for CL.GameNetQuery. Everything here is pure: the status PARSERS
// operate on raw console strings, and the RCON packet types are value serialization.
// The live UDP query clients need a real game server and are not tested here; the RCON clients
// are tested against a loopback fake server in RconFramingTests.cs.

public class ValveStatusParserTests
{
    // A realistic Source-engine (CSS) `status` console dump. The fixture is derived directly
    // from the regexes in ValveStatusParser: player lines use the classic
    // `# userid "name" uniqueid connected ping loss state adr` shape with the name in quotes.
    private const string CssStatus =
        "hostname: My Awesome CSS Server\n" +
        "version : 1.0.0.34/24 6867 secure\n" +
        "udp/ip  : 192.168.1.50:27015  (public ip: 203.0.113.7)\n" +
        "map     : de_dust2 at: 0 x, 0 y, 0 z\n" +
        "tags    : alltalk,nocheats,classic\n" +
        "players : 2 humans, 1 bots (10/0 max)\n" +
        "# userid name uniqueid connected ping loss state adr\n" +
        "#  2 \"Alice\" STEAM_1:0:11101 01:23 45 0 active 198.51.100.2:27005\n" +
        "#  3 \"Bob\" STEAM_1:0:22202 12:00 67 1 active 198.51.100.3:27005\n" +
        "#  4 \"HALReplica\" BOT active\n";

    [Fact]
    public void GetHostname_extracts_name()
        => Assert.Equal("My Awesome CSS Server", ValveStatusParser.GetHostname(CssStatus));

    [Fact]
    public void GetMapName_extracts_map()
        => Assert.Equal("de_dust2", ValveStatusParser.GetMapName(CssStatus));

    [Fact]
    public void GetPlayerCount_returns_humans_and_bots()
    {
        var (humans, bots) = ValveStatusParser.GetPlayerCount(CssStatus);
        Assert.Equal(2, humans);
        Assert.Equal(1, bots);
    }

    [Fact]
    public void GetServerAddress_returns_ip_and_port()
    {
        var (ip, port) = ValveStatusParser.GetServerAddress(CssStatus);
        Assert.Equal("192.168.1.50", ip);
        Assert.Equal(27015, port);
    }

    [Fact]
    public void GetVersion_extracts_version()
        => Assert.Equal("1.0.0.34/24", ValveStatusParser.GetVersion(CssStatus));

    [Fact]
    public void GetTags_splits_on_comma()
    {
        var tags = ValveStatusParser.GetTags(CssStatus);
        Assert.Equal(new[] { "alltalk", "nocheats", "classic" }, tags);
    }

    [Fact]
    public void GetPlayerList_parses_names_and_bot_flag()
    {
        var players = ValveStatusParser.GetPlayerList(CssStatus);
        Assert.Equal(3, players.Count);

        var names = players.Select(p => p.Name).ToList();
        Assert.Contains("Alice", names);
        Assert.Contains("Bob", names);
        Assert.Contains("HALReplica", names);

        var alice = players.Single(p => p.Name == "Alice");
        Assert.Equal("STEAM_1:0:11101", alice.UniqueId);
        Assert.Equal(45, alice.Ping);
        Assert.False(alice.IsBot);

        var bot = players.Single(p => p.Name == "HALReplica");
        Assert.True(bot.IsBot);
        Assert.Equal("BOT", bot.UniqueId);
    }

    [Fact]
    public void ParseStatus_populates_server_info()
    {
        var info = ValveStatusParser.ParseStatus(CssStatus);
        Assert.Equal("My Awesome CSS Server", info.Hostname);
        Assert.Equal("de_dust2", info.Map);
        Assert.Equal(2, info.PlayerCount);
        Assert.Equal(1, info.BotCount);
        Assert.Equal("192.168.1.50", info.Ip);
        Assert.Equal(27015, info.Port);
        Assert.True(info.IsOnline);
        Assert.Equal(CssStatus, info.RawResponse);
    }

    [Fact]
    public void ParseStatusWithPlayers_returns_info_and_players()
    {
        var (info, players) = ValveStatusParser.ParseStatusWithPlayers(CssStatus);
        Assert.Equal("de_dust2", info.Map);
        Assert.Equal(3, players.Count);
    }

    [Fact]
    public void Empty_input_yields_sane_defaults()
    {
        Assert.Equal(string.Empty, ValveStatusParser.GetHostname(""));
        Assert.Equal(string.Empty, ValveStatusParser.GetMapName(""));
        Assert.Equal((0, 0), ValveStatusParser.GetPlayerCount(""));
        Assert.Equal((string.Empty, 0), ValveStatusParser.GetServerAddress(""));
        Assert.Empty(ValveStatusParser.GetTags(""));
        Assert.Empty(ValveStatusParser.GetPlayerList(""));

        var info = ValveStatusParser.ParseStatus("");
        Assert.False(info.IsOnline);
    }
}

public class ValveStatusParserCS2Tests
{
    // CS2 `status` output. Player rows match the CS2 regex:
    //   \s+(\d+)\s+(BOT|\d+)\s+(\d+)\s+(\d+)\s+(\w+)\s+(\d+)\s+'(.+?)'
    //   => id, steamid-or-BOT, ping, loss, state(\w+), <num>, 'name'
    // Map/spawngroup rows match the "loaded spawngroup(...): SV: [N: name | main lump" shape.
    private const string Cs2Status =
        "hostname: CS2 Dedicated Server\n" +
        "players : 1 humans, 1 bots (12/0 max)\n" +
        "loaded spawngroup(  1): SV:  [1: de_inferno | main lump ]\n" +
        "id              steamid                 name\n" +
        "  0 1234567890 25 0 active 0 'Carol'\n" +
        "  1 BOT 0 0 active 0 'Dave'\n";

    [Fact]
    public void GetHostname_works()
        => Assert.Equal("CS2 Dedicated Server", ValveStatusParserCS2.GetHostname(Cs2Status));

    [Fact]
    public void GetMapName_reads_spawngroup()
        => Assert.Equal("de_inferno", ValveStatusParserCS2.GetMapName(Cs2Status));

    [Fact]
    public void GetPlayerCount_works()
    {
        var (humans, bots) = ValveStatusParserCS2.GetPlayerCount(Cs2Status);
        Assert.Equal(1, humans);
        Assert.Equal(1, bots);
    }

    [Fact]
    public void GetSpawngroups_lists_loaded_groups()
    {
        var groups = ValveStatusParserCS2.GetSpawngroups(Cs2Status);
        Assert.Contains("de_inferno", groups);
    }

    [Fact]
    public void GetPlayerList_parses_cs2_rows()
    {
        var players = ValveStatusParserCS2.GetPlayerList(Cs2Status);
        var names = players.Select(p => p.Name).ToList();
        Assert.Contains("Carol", names);
        Assert.Contains("Dave", names);

        var dave = players.Single(p => p.Name == "Dave");
        Assert.True(dave.IsBot);

        var carol = players.Single(p => p.Name == "Carol");
        Assert.False(carol.IsBot);
        Assert.Equal(25, carol.Ping);
    }

    [Fact]
    public void Empty_input_does_not_throw_and_returns_defaults()
    {
        Assert.Equal(string.Empty, ValveStatusParserCS2.GetHostname(""));
        Assert.Equal(string.Empty, ValveStatusParserCS2.GetMapName(""));
        Assert.Empty(ValveStatusParserCS2.GetSpawngroups(""));
        Assert.Empty(ValveStatusParserCS2.GetPlayerList(""));
    }
}

public class RconPacketTests
{
    [Fact]
    public void ToBytes_FromBytes_round_trips()
    {
        var packet = RconPacket.Create(42, RconPacketType.ExecCommand, "status");
        var bytes = packet.ToBytes();
        var back = RconPacket.FromBytes(bytes);

        Assert.Equal(42, back.Id);
        Assert.Equal(RconPacketType.ExecCommand, back.Type);
        Assert.Equal("status", back.Body);
    }

    [Fact]
    public void Round_trip_with_explicit_encoding()
    {
        var packet = RconPacket.Create(7, RconPacketType.Auth, "p@ssw0rd");
        var back = RconPacket.FromBytes(packet.ToBytes(Encoding.ASCII), Encoding.ASCII);

        Assert.Equal(7, back.Id);
        Assert.Equal(RconPacketType.Auth, back.Type);
        Assert.Equal("p@ssw0rd", back.Body);
    }

    [Fact]
    public void First_four_bytes_encode_remaining_length()
    {
        var packet = RconPacket.Create(1, RconPacketType.Response, "hi");
        var bytes = packet.ToBytes();
        var declaredSize = BitConverter.ToInt32(bytes, 0);
        // Size field is the byte count following itself: total length minus the 4 size bytes.
        Assert.Equal(bytes.Length - 4, declaredSize);
    }

    [Fact]
    public void Empty_content_is_substituted_with_null_terminator()
    {
        var packet = RconPacket.Create(5, RconPacketType.ExecCommand, "");
        // Empty content becomes "\0"; after FromBytes strips nulls the body is empty again.
        var back = RconPacket.FromBytes(packet.ToBytes());
        Assert.Equal(5, back.Id);
        Assert.Equal(string.Empty, back.Body);
    }
}

public class MinecraftRconPacketTests
{
    [Fact]
    public void ToBytes_FromBytes_round_trips()
    {
        var packet = MinecraftRconPacket.Create(99, MinecraftPacketType.ExecCommand, "list");
        var back = MinecraftRconPacket.FromBytes(packet.ToBytes());

        Assert.Equal(99, back.Id);
        Assert.Equal(MinecraftPacketType.ExecCommand, back.Type);
        Assert.Equal("list", back.Body);
    }

    [Fact]
    public void Auth_packet_round_trips()
    {
        var packet = MinecraftRconPacket.Create(3, MinecraftPacketType.Auth, "secret");
        var back = MinecraftRconPacket.FromBytes(packet.ToBytes());

        Assert.Equal(3, back.Id);
        Assert.Equal(MinecraftPacketType.Auth, back.Type);
        Assert.Equal("secret", back.Body);
    }
}

public class GameNetQueryLibraryTests
{
    [Fact]
    public async Task HealthCheck_is_healthy()
    {
        var lib = new GameNetQueryLibrary();
        var health = await lib.HealthCheckAsync();
        Assert.Equal(HealthStatusLevel.Healthy, health.Status);
    }

    [Fact]
    public void Manifest_identifies_the_library()
    {
        var lib = new GameNetQueryLibrary();
        Assert.Equal("cl.gamenetquery", lib.Manifest.Id);
        Assert.Equal("CL.GameNetQuery", lib.Manifest.Name);
    }
}

/// <summary>
/// <see cref="ValveUdpQuery.QueryAsync"/> against a fake A2S server on loopback, so the
/// <c>Result&lt;ServerQuery&gt;</c> shape and every error kind are tested offline.
/// </summary>
public class ValveUdpQueryResultTests
{
    private static readonly byte[] Header = [0xFF, 0xFF, 0xFF, 0xFF];

    private static byte[] InfoResponse()
    {
        var b = new List<byte>(Header) { 0x49, 17 };
        foreach (var s in new[] { "Fake Server", "de_inferno", "cstrike", "Counter-Strike" })
        {
            b.AddRange(Encoding.UTF8.GetBytes(s));
            b.Add(0);
        }
        b.AddRange(BitConverter.GetBytes((short)240));
        b.AddRange([3, 24, 1]); // players, max, bots
        return [.. b];
    }

    private static byte[] PlayerResponse()
    {
        var b = new List<byte>(Header) { 0x44, 1, 0 };
        b.AddRange(Encoding.UTF8.GetBytes("Alice"));
        b.Add(0);
        b.AddRange(BitConverter.GetBytes(42));
        b.AddRange(BitConverter.GetBytes(120.5f));
        return [.. b];
    }

    private static byte[] Challenge(int value) => [.. Header, 0x41, .. BitConverter.GetBytes(value)];

    /// <summary>Starts a loopback UDP server that answers each request with <paramref name="reply"/> (null = stay silent).</summary>
    private static (System.Net.Sockets.UdpClient Server, ushort Port) FakeServer(Func<byte[], byte[]?> reply)
    {
        var server = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var req = await server.ReceiveAsync();
                    var answer = reply(req.Buffer);
                    if (answer is not null)
                        await server.SendAsync(answer, answer.Length, req.RemoteEndPoint);
                }
            }
            catch (ObjectDisposedException) { }
            catch (System.Net.Sockets.SocketException) { }
        });
        return (server, (ushort)((System.Net.IPEndPoint)server.Client.LocalEndPoint!).Port);
    }

    private static byte[]? A2S(byte[] req, bool infoChallenge) => req[4] switch
    {
        0x54 when infoChallenge && req.Length == 25 => Challenge(0x1234), // first A2S_INFO, no challenge yet
        0x54 => InfoResponse(),
        0x55 when BitConverter.ToInt32(req, 5) == -1 => Challenge(0x5678),
        0x55 => PlayerResponse(),
        _ => null,
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Query_returns_info_players_and_duration(bool infoChallenge)
    {
        var (server, port) = FakeServer(req => A2S(req, infoChallenge));
        using var _ = server;

        var r = await ValveUdpQuery.QueryAsync("127.0.0.1", port, timeoutMs: 2000);

        Assert.True(r.IsSuccess, r.IsFailure ? r.Error!.ToString() : null);
        Assert.Equal("Fake Server", r.Value!.Info.Hostname);
        Assert.Equal("de_inferno", r.Value.Info.Map);
        Assert.Equal(24, r.Value.Info.MaxPlayers);
        var player = Assert.Single(r.Value.Players);
        Assert.Equal("Alice", player.Name);
        Assert.Equal(42, player.Score);
        Assert.True(r.Value.DurationMs >= 0);
    }

    [Fact]
    public async Task Query_without_players_skips_the_player_round_trip()
    {
        var seen = new List<byte>();
        var (server, port) = FakeServer(req => { lock (seen) seen.Add(req[4]); return A2S(req, false); });
        using var _ = server;

        var r = await ValveUdpQuery.QueryAsync("127.0.0.1", port, includePlayers: false, timeoutMs: 2000);

        Assert.True(r.IsSuccess);
        Assert.Empty(r.Value!.Players);
        lock (seen) Assert.DoesNotContain((byte)0x55, seen);
    }

    [Fact]
    public async Task Silent_server_is_a_timeout_error()
    {
        var (server, port) = FakeServer(_ => null);
        using var _ = server;

        var r = await ValveUdpQuery.QueryAsync("127.0.0.1", port, timeoutMs: 200);

        Assert.IsType<CodeLogic.Core.Results.TimeoutError>(r.Error);
        Assert.Equal("gamenetquery.timeout", r.Error!.Code);
    }

    [Fact]
    public async Task Garbage_answer_is_a_malformed_response()
    {
        var (server, port) = FakeServer(_ => Encoding.ASCII.GetBytes("hello, not a2s"));
        using var _ = server;

        var r = await ValveUdpQuery.QueryAsync("127.0.0.1", port, timeoutMs: 2000);

        Assert.IsType<CodeLogic.Core.Results.InternalError>(r.Error);
        Assert.Equal("gamenetquery.malformed_response", r.Error!.Code);
    }

    [Theory]
    [InlineData("not-an-ip", 1000, "gamenetquery.invalid_address")]
    [InlineData("127.0.0.1", 0, "gamenetquery.invalid_timeout")]
    public async Task Bad_input_is_a_validation_error(string ip, int timeoutMs, string code)
    {
        var r = await ValveUdpQuery.QueryAsync(ip, 27015, timeoutMs: timeoutMs);
        Assert.IsType<CodeLogic.Core.Results.ValidationError>(r.Error);
        Assert.Equal(code, r.Error!.Code);
    }

    [Fact]
    public async Task Closed_port_is_unavailable_or_timeout()
    {
        // Grab a free port, then close it. Windows reports ICMP port-unreachable as a socket
        // error (UnavailableError); other platforms just never answer (TimeoutError).
        var (server, port) = FakeServer(_ => null);
        server.Dispose();

        var r = await ValveUdpQuery.QueryAsync("127.0.0.1", port, timeoutMs: 300);

        Assert.True(r.Error is CodeLogic.Core.Results.UnavailableError or CodeLogic.Core.Results.TimeoutError,
            r.Error?.ToString());
    }

    private static byte[] FullInfoResponse(string name = "Fake Server")
    {
        var b = new List<byte>(Header) { 0x49, 17 };
        foreach (var s in new[] { name, "de_inferno", "csgo", "Counter-Strike 2" })
        {
            b.AddRange(Encoding.UTF8.GetBytes(s));
            b.Add(0);
        }
        b.AddRange(BitConverter.GetBytes((short)730));
        b.AddRange([3, 24, 1]);                       // players, max, bots
        b.AddRange([(byte)'d', (byte)'l', 0, 1]);     // server type, environment, visibility, VAC
        b.AddRange(Encoding.UTF8.GetBytes("1.40.2.1\0"));
        b.Add(0x80 | 0x10 | 0x40 | 0x20 | 0x01);      // EDF: port, SteamID, SourceTV, keywords, game id
        b.AddRange(BitConverter.GetBytes((short)27015));
        b.AddRange(BitConverter.GetBytes(90071992547409920L));
        b.AddRange(BitConverter.GetBytes((short)27020));
        b.AddRange(Encoding.UTF8.GetBytes("SourceTV\0"));
        b.AddRange(Encoding.UTF8.GetBytes("secure, competitive,valve_ds\0"));
        b.AddRange(BitConverter.GetBytes(730L));
        return [.. b];
    }

    [Fact]
    public async Task Version_and_tags_come_from_the_A2S_INFO_tail()
    {
        var (server, port) = FakeServer(req => req[4] == 0x54 ? FullInfoResponse() : null);
        using var _ = server;

        var r = await ValveUdpQuery.QueryAsync("127.0.0.1", port, includePlayers: false, timeoutMs: 2000);

        Assert.True(r.IsSuccess, r.IsFailure ? r.Error!.ToString() : null);
        Assert.Equal("1.40.2.1", r.Value!.Info.Version);
        Assert.Equal(["secure", "competitive", "valve_ds"], r.Value.Info.Tags);
        Assert.Equal(3, r.Value.Info.PlayerCount);
    }

    [Fact]
    public async Task A_reply_from_another_endpoint_is_ignored()
    {
        // A third party that sees (or guesses) the client's port answers first; only the queried server counts.
        using var rogue = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        var server = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        using var serverScope = server;
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var req = await server.ReceiveAsync();
                    var spoof = FullInfoResponse("Spoofed");
                    await rogue.SendAsync(spoof, spoof.Length, req.RemoteEndPoint);
                    await Task.Delay(50);
                    var real = FullInfoResponse();
                    await server.SendAsync(real, real.Length, req.RemoteEndPoint);
                }
            }
            catch (ObjectDisposedException) { }
            catch (System.Net.Sockets.SocketException) { }
        });
        var port = (ushort)((System.Net.IPEndPoint)server.Client.LocalEndPoint!).Port;

        var r = await ValveUdpQuery.QueryAsync("127.0.0.1", port, includePlayers: false, timeoutMs: 2000);

        Assert.True(r.IsSuccess, r.IsFailure ? r.Error!.ToString() : null);
        Assert.Equal("Fake Server", r.Value!.Info.Hostname);
    }

    [Fact]
    public void Endpoint_match_treats_ipv4_mapped_addresses_as_ipv4()
    {
        var expected = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("203.0.113.7"), 27015);
        Assert.True(ValveUdpQuery.IsFrom(new(System.Net.IPAddress.Parse("::ffff:203.0.113.7"), 27015), expected));
        Assert.False(ValveUdpQuery.IsFrom(new(System.Net.IPAddress.Parse("203.0.113.8"), 27015), expected));
        Assert.False(ValveUdpQuery.IsFrom(new(System.Net.IPAddress.Parse("203.0.113.7"), 27016), expected));
    }

    [Fact]
    public async Task Unterminated_string_is_a_malformed_response()
    {
        byte[] truncated = [.. Header, 0x49, 17, .. Encoding.UTF8.GetBytes("no terminator")];
        var (server, port) = FakeServer(_ => truncated);
        using var _ = server;

        var r = await ValveUdpQuery.QueryAsync("127.0.0.1", port, includePlayers: false, timeoutMs: 2000);

        Assert.Equal("gamenetquery.malformed_response", r.Error!.Code);
    }

    [Fact]
    public void ReadNullTerminatedString_stays_inside_the_buffer()
    {
        byte[] data = [(byte)'a', (byte)'b', 0, (byte)'c'];
        var index = 0;
        Assert.Equal("ab", ValveUdpQuery.ReadNullTerminatedString(data, ref index));
        Assert.Equal(3, index);
        Assert.Throws<InvalidDataException>(() => ValveUdpQuery.ReadNullTerminatedString(data, ref index));   // "c" has no NUL
        index = 4;
        Assert.Throws<InvalidDataException>(() => ValveUdpQuery.ReadNullTerminatedString(data, ref index));   // at the end
        index = 9;
        Assert.Throws<InvalidDataException>(() => ValveUdpQuery.ReadNullTerminatedString(data, ref index));   // past the end
    }

    [Fact]
    public async Task Legacy_helpers_still_return_null_and_empty_on_failure()
    {
        var (server, port) = FakeServer(_ => null);
        using var _ = server;
        Assert.Null(await ValveUdpQuery.GetServerInfoAsync("127.0.0.1", port, 200));
        Assert.Empty(await ValveUdpQuery.GetPlayerListAsync("127.0.0.1", port, 200));
    }
}
