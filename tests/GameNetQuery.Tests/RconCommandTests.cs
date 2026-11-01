using CL.GameNetQuery;
using CL.GameNetQuery.Valve;
using CL.GameNetQuery.Valve.Games;
using CodeLogic.Core.Results;
using Xunit;

namespace GameNetQuery.Tests;

/// <summary>
/// The Counter-Strike wrappers splice caller values into console lines. These tests pin the exact line sent
/// for good input and prove that injection payloads (a second command after <c>;</c> or a line break, a closing
/// quote, NUL, ...) are rejected with a validation error before anything reaches the server.
/// </summary>
public class RconCommandTests
{
    private const int Auth = 3, AuthResponse = 2, Exec = 2, Response = 0;

    private static IEnumerable<byte[]> Server(RconRequest r) => r.Type switch
    {
        Auth => [FakeRconServer.Packet(r.Id, Response, ""), FakeRconServer.Packet(r.Body == "pw" ? r.Id : -1, AuthResponse, "")],
        Exec => [FakeRconServer.Packet(r.Id, Response, "ok")],
        _ => [FakeRconServer.Packet(r.Id, Response, "")],     // sentinel echo
    };

    private static string[] Commands(FakeRconServer server)
    {
        lock (server.Requests)
            return [.. server.Requests.Where(r => r.Type == Exec).Select(r => r.Body)];
    }

    public static TheoryData<string, Func<CounterStrike2, Task<Result<string>>>> Cs2Valid => new()
    {
        { "changelevel de_dust2", c => c.ChangeMapAsync("de_dust2") },
        { "changelevel workshop/123456/de_cache", c => c.ChangeMapAsync("workshop/123456/de_cache") },
        { "kick Alice Smith", c => c.KickPlayerAsync("Alice Smith") },
        { "kickid 7", c => c.KickPlayerAsync(7) },
        { "kickid 7 Team killing", c => c.KickPlayerAsync(7, "Team killing") },
        { "banid 0 STEAM_1:0:123456", c => c.BanPlayerAsync("STEAM_1:0:123456") },
        { "banid 0 [U:1:246912]", c => c.BanPlayerAsync("[U:1:246912]") },
        { "banid 0 12", c => c.BanPlayerAsync(12) },
        { "banid 30 12", c => c.BanPlayerAsync(12, minutes: 30) },
        { "removeid 76561197960265728", c => c.UnbanPlayerAsync("76561197960265728") },
        { "hostname \"My Server | 128 tick\"", c => c.SetHostnameAsync("My Server | 128 tick") },
        { "exec gamemode_competitive.cfg", c => c.ExecConfigAsync("gamemode_competitive.cfg") },
        { "mp_roundtime \"1.92\"", c => c.SetCvarAsync("mp_roundtime", "1.92") },
        { "sv_password \"\"", c => c.SetCvarAsync("sv_password", "") },
        { "mp_roundtime", c => c.GetCvarAsync("mp_roundtime") },
        { "say Hello, world!", c => c.SayAsync("Hello, world!") },
        { "slay Bob", c => c.SlayPlayerAsync("Bob") },
    };

    [Theory]
    [MemberData(nameof(Cs2Valid))]
    public async Task Cs2_sends_the_exact_command(string expected, Func<CounterStrike2, Task<Result<string>>> act)
    {
        await using var server = new FakeRconServer(Server);
        using var cs2 = new CounterStrike2("127.0.0.1", server.Port, "pw");
        Assert.True(await cs2.ConnectAsync());

        var r = await act(cs2);

        Assert.True(r.IsSuccess, r.IsFailure ? r.Error!.ToString() : null);
        Assert.Equal("ok", r.Value);
        Assert.Equal([expected], Commands(server));
    }

    public static TheoryData<string, Func<CounterStrike2, Task<Result<string>>>> Cs2Injections => new()
    {
        { "kick ;", c => c.KickPlayerAsync("x; rcon_password pwned") },
        { "kick LF", c => c.KickPlayerAsync("x\nrcon_password pwned") },
        { "kick CR", c => c.KickPlayerAsync("x\rquit") },
        { "kick NUL", c => c.KickPlayerAsync("x\0quit") },
        { "kick quote", c => c.KickPlayerAsync("x\" ; quit") },
        { "kick empty", c => c.KickPlayerAsync("") },
        { "kickid reason ;", c => c.KickPlayerAsync(3, "bye; quit") },
        { "kickid negative", c => c.KickPlayerAsync(-1) },
        { "ban name", c => c.BanPlayerAsync("Alice") },
        { "ban ;", c => c.BanPlayerAsync("STEAM_1:0:1; quit") },
        { "ban negative minutes", c => c.BanPlayerAsync(3, -5) },
        { "unban ;", c => c.UnbanPlayerAsync("1;quit") },
        { "say ;", c => c.SayAsync("gg; sv_cheats 1") },
        { "say LF", c => c.SayAsync("gg\nsv_cheats 1") },
        { "say tab", c => c.SayAsync("a\tb") },
        { "say C1", c => c.SayAsync("a\u0085b") },
        { "hostname quote", c => c.SetHostnameAsync("x\"; rcon_password pwned; echo \"") },
        { "map ;", c => c.ChangeMapAsync("de_dust2;quit") },
        { "map space", c => c.ChangeMapAsync("de_dust2 quit") },
        { "map traversal", c => c.ChangeMapAsync("../../secret") },
        { "exec traversal", c => c.ExecConfigAsync("../server") },
        { "exec absolute", c => c.ExecConfigAsync("/etc/passwd") },
        { "exec ;", c => c.ExecConfigAsync("a;quit") },
        { "cvar name ;", c => c.SetCvarAsync("sv_cheats 1;rcon_password", "x") },
        { "cvar value ;", c => c.SetCvarAsync("hostname", "x; quit") },
        { "cvar value quote", c => c.SetCvarAsync("hostname", "x\" quit \"") },
        { "get cvar ;", c => c.GetCvarAsync("sv_cheats;quit") },
        { "slay LF", c => c.SlayPlayerAsync("Bob\nquit") },
    };

    [Theory]
    [MemberData(nameof(Cs2Injections))]
    public async Task Cs2_rejects_unsafe_arguments_without_sending(string payload, Func<CounterStrike2, Task<Result<string>>> act)
    {
        await using var server = new FakeRconServer(Server);
        using var cs2 = new CounterStrike2("127.0.0.1", server.Port, "pw");
        Assert.True(await cs2.ConnectAsync());

        var r = await act(cs2);

        var error = Assert.IsType<ValidationError>(r.Error);
        Assert.Equal(GameNetQueryErrorCodes.InvalidRconArgument, error.Code);
        Assert.True(Commands(server).Length == 0, $"{payload}: a command was sent");
    }

    [Fact]
    public async Task Validation_happens_before_the_connection_check()
    {
        using var cs2 = new CounterStrike2("127.0.0.1", 1, "pw");   // never connected
        var r = await cs2.KickPlayerAsync("x; quit");
        Assert.IsType<ValidationError>(r.Error);
    }

    [Fact]
    public async Task Css_shares_the_safe_command_builders()
    {
        await using var server = new FakeRconServer(Server);
        using var css = new CounterStrikeSource("127.0.0.1", server.Port, "pw");
        Assert.True(await css.ConnectAsync());

        Assert.True((await css.KickPlayerAsync(4, "afk")).IsSuccess);
        Assert.True((await css.SetCvarAsync("mp_timelimit", "30")).IsSuccess);
        Assert.True((await css.ChangeMapAsync("de_nuke")).IsSuccess);
        Assert.True((await css.BanPlayerAsync("STEAM_0:1:42")).IsSuccess);
        Assert.IsType<ValidationError>((await css.KickPlayerAsync("x; rcon_password pwned")).Error);
        Assert.IsType<ValidationError>((await css.SayAsync("a\nquit")).Error);
        Assert.IsType<ValidationError>((await css.SetHostnameAsync("a\"b")).Error);
        Assert.IsType<ValidationError>((await css.ExecConfigAsync("../x")).Error);

        Assert.Equal(["kickid 4 afk", "mp_timelimit \"30\"", "changelevel de_nuke", "banid 0 STEAM_0:1:42"], Commands(server));
    }

    [Fact]
    public void Status_parsers_report_the_user_id_for_kickid()
    {
        const string css = "# userid name uniqueid connected ping loss state adr\n" +
                           "#  2 \"x; quit\" STEAM_1:0:11101 01:23 45 0 active 198.51.100.2:27005\n";
        var player = Assert.Single(ValveStatusParser.GetPlayerList(css));
        Assert.Equal(2, player.UserId);
        Assert.Equal("x; quit", player.Name);   // a hostile name: kick it by id, not by name

        var cs2 = Assert.Single(ValveStatusParserCS2.GetPlayerList("  5 1234567890 25 0 active 0 'Carol'\n"));
        Assert.Equal(5, cs2.UserId);
    }
}
