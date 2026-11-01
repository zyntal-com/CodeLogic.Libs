using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CL.GameNetQuery.Minecraft;
using Xunit;

namespace GameNetQuery.Tests;

public class MinecraftQueryTests
{
    /// <summary>
    /// A full-stat reply from a vanilla server, byte for byte in the wire layout (session id 0x01020304):
    /// type 0x00, session id, <c>splitnum\0\x80\0</c>, key/value pairs ended by an empty key,
    /// <c>\x01player_\0\0</c>, player names ended by an empty name. The MOTD carries a § color code.
    /// </summary>
    private static readonly byte[] FullStatSample = Convert.FromHexString(
        "00" + "01020304" +
        "73706C69746E756D008000" +                                           // splitnum\0 0x80 \0
        "686F73746E616D6500C2A7614120C2A76C4D696E65637261667420536572766572" + "00" + // hostname\0 "§aA §lMinecraft Server"\0
        "67616D657479706500534D5000" +                                       // gametype\0SMP\0
        "67616D655F6964004D494E45435241465400" +                             // game_id\0MINECRAFT\0
        "76657273696F6E00312E32302E3400" +                                   // version\01.20.4\0
        "706C7567696E730000" +                                               // plugins\0\0
        "6D617000776F726C6400" +                                             // map\0world\0
        "6E756D706C61796572730032" + "00" +                                  // numplayers\02\0
        "6D6178706C617965727300323000" +                                     // maxplayers\020\0
        "686F7374706F7274003235353635" + "00" +                              // hostport\025565\0
        "686F73746970003132372E302E302E3100" +                               // hostip\0127.0.0.1\0
        "00" +                                                               // end of key/value section
        "01706C617965725F0000" +                                             // \x01player_\0\0
        "4E6F74636800" + "6A65625F00" +                                      // Notch\0 jeb_\0
        "00");                                                               // end of players

    [Fact]
    public void Full_stat_sample_parses_into_keys_and_players()
    {
        var stat = MinecraftQueryClient.ParseFullStat(FullStatSample);

        Assert.NotNull(stat);
        var (values, players) = stat.Value;
        Assert.Equal("SMP", values["gametype"]);
        Assert.Equal("1.20.4", values["version"]);
        Assert.Equal("", values["plugins"]);
        Assert.Equal("world", values["map"]);
        Assert.Equal("2", values["numplayers"]);
        Assert.Equal("20", values["maxplayers"]);
        Assert.Equal("25565", values["hostport"]);
        Assert.Equal(["Notch", "jeb_"], players);
    }

    [Fact]
    public void Basic_stat_reply_is_not_mistaken_for_full_stat()
    {
        // A basic-stat reply: type, session id, then bare values with no keys (MOTD, gametype, map, counts, ...).
        byte[] basic = [0x00, 1, 2, 3, 4, .. Encoding.ASCII.GetBytes("A Minecraft Server\0SMP\0world\02\020\0"), 0xDD, 0x63, .. "127.0.0.1\0"u8];
        Assert.Null(MinecraftQueryClient.ParseFullStat(basic));
    }

    [Fact]
    public void Truncated_full_stat_is_rejected()
    {
        Assert.Null(MinecraftQueryClient.ParseFullStat(FullStatSample[..40]));
        Assert.Null(MinecraftQueryClient.ParseFullStat([0x00, 1, 2]));
    }

    [Fact]
    public void Full_stat_request_carries_the_four_padding_bytes()
    {
        var request = MinecraftQueryClient.CreateFullStatRequest(0x01020304, 9513307);
        Assert.Equal("FEFD00" + "01020304" + "0091295B" + "00000000", Convert.ToHexString(request));
        Assert.Equal("FEFD09" + "01020304", Convert.ToHexString(MinecraftQueryClient.CreateHandshakeRequest(0x01020304)));
    }

    [Theory]
    [InlineData("\t9513307\0", false)]
    [InlineData("9513307\0", true)]
    [InlineData("-123\0", true)]
    [InlineData("abc\0", false)]
    public void Challenge_token_is_parsed_safely(string token, bool ok)
    {
        byte[] response = [0x09, 1, 2, 3, 4, .. Encoding.ASCII.GetBytes(token)];
        Assert.Equal(ok, MinecraftQueryClient.TryParseChallengeToken(response, out _));
    }

    [Fact]
    public async Task QueryServer_end_to_end_returns_the_documented_keys()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var requests = new List<byte[]>();
        var loop = Task.Run(() =>
        {
            try
            {
                for (var i = 0; i < 2; i++)
                {
                    var from = new IPEndPoint(IPAddress.Any, 0);
                    var req = server.Receive(ref from);
                    lock (requests) requests.Add(req);
                    var session = req.AsSpan(3, 4).ToArray();
                    byte[] reply = req[2] == 0x09
                        ? [0x09, .. session, .. "9513307\0"u8]
                        : req.Length == 15                                   // full stat (with padding)
                            ? [.. FullStatSample[..1], .. session, .. FullStatSample[5..]]
                            : [0x00, .. session, .. "A Minecraft Server\0SMP\0world\0"u8];
                    server.Send(reply, reply.Length, from);
                }
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        });
        var port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;

        var response = MinecraftQueryClient.QueryServer(IPAddress.Loopback, port);
        await loop.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("A Minecraft Server", MinecraftQueryClient.GetStatusValue(response, "hostname"));   // § codes stripped
        Assert.Equal("world", MinecraftQueryClient.GetStatusValue(response, "map"));
        Assert.Equal("2", MinecraftQueryClient.GetStatusValue(response, "numplayers"));
        Assert.Equal("20", MinecraftQueryClient.GetStatusValue(response, "maxplayers"));
        Assert.Equal("Notch, jeb_", MinecraftQueryClient.GetStatusValue(response, "players"));
        lock (requests)
        {
            Assert.Equal(2, requests.Count);
            Assert.Equal(15, requests[1].Length);
            Assert.Equal(9513307, BinaryPrimitives.ReadInt32BigEndian(requests[1].AsSpan(7)));
        }
    }

    [Fact]
    public void QueryServer_returns_empty_for_a_host_name_or_silent_server()
    {
        Assert.Equal(string.Empty, MinecraftQueryClient.QueryServer("mc.example.com", 25565));
    }
}
