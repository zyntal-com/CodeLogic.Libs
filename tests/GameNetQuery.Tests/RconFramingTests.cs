using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CL.GameNetQuery;
using CL.GameNetQuery.Minecraft;
using CL.GameNetQuery.Valve;
using Xunit;

namespace GameNetQuery.Tests;

/// <summary>A received request: id, type and decoded body.</summary>
internal readonly record struct RconRequest(int Id, int Type, string Body);

/// <summary>
/// A loopback RCON server for tests. Each request goes to <c>handler</c>, which returns the packets to
/// send back. Replies are written a few bytes at a time with pauses (and Nagle disabled), so the client
/// sees packets split across TCP reads and several packets glued together.
/// </summary>
internal sealed class FakeRconServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Func<RconRequest, IEnumerable<byte[]>> _handler;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly int _chunk;

    public List<RconRequest> Requests { get; } = [];

    public FakeRconServer(Func<RconRequest, IEnumerable<byte[]>> handler, int chunk = 3)
    {
        _handler = handler;
        _chunk = chunk;
        _listener.Start();
        _loop = Task.Run(RunAsync);
    }

    public ushort Port => (ushort)((IPEndPoint)_listener.LocalEndpoint).Port;

    public static byte[] Packet(int id, int type, string body) => RconSession.Build(id, type, body);

    private async Task RunAsync()
    {
        try
        {
            using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
            client.NoDelay = true;
            var stream = client.GetStream();
            var header = new byte[4];
            while (!_cts.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(header, _cts.Token);
                var rest = new byte[BinaryPrimitives.ReadInt32LittleEndian(header)];
                await stream.ReadExactlyAsync(rest, _cts.Token);
                var request = new RconRequest(
                    BinaryPrimitives.ReadInt32LittleEndian(rest),
                    BinaryPrimitives.ReadInt32LittleEndian(rest.AsSpan(4)),
                    Encoding.UTF8.GetString(rest.AsSpan(8)).TrimEnd('\0'));
                lock (Requests) Requests.Add(request);

                var reply = _handler(request).SelectMany(p => p).ToArray();
                for (var i = 0; i < reply.Length; i += _chunk)
                {
                    await stream.WriteAsync(reply.AsMemory(i, Math.Min(_chunk, reply.Length - i)), _cts.Token);
                    await stream.FlushAsync(_cts.Token);
                    if (i % (_chunk * 50) == 0) await Task.Delay(1, _cts.Token);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or EndOfStreamException or SocketException or ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        try { await _loop; } catch { /* shutting down */ }
        _cts.Dispose();
    }
}

public class RconFrameReaderTests
{
    /// <summary>A stream that hands out one byte per read.</summary>
    private sealed class TrickleStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }

    [Fact]
    public async Task Reassembles_packets_split_into_single_bytes()
    {
        var data = FakeRconServer.Packet(7, 0, "hello").Concat(FakeRconServer.Packet(8, 2, new string('x', 5000))).ToArray();
        var reader = new RconFrameReader(new TrickleStream(data));

        var first = await reader.ReadAsync(CancellationToken.None);
        var second = await reader.ReadAsync(CancellationToken.None);

        Assert.Equal((7, 0, "hello"), (first.Id, first.Type, RconSession.DecodeBody(first.Packet)));
        Assert.Equal((8, 2), (second.Id, second.Type));
        Assert.Equal(new string('x', 5000), RconSession.DecodeBody(second.Packet));
    }

    [Fact]
    public async Task Rejects_out_of_range_size()
    {
        var reader = new RconFrameReader(new MemoryStream([5, 0, 0, 0, 1, 2, 3, 4, 5]));
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Truncated_stream_is_end_of_stream()
    {
        var packet = FakeRconServer.Packet(1, 0, "abc");
        var reader = new RconFrameReader(new MemoryStream(packet[..^3]));
        await Assert.ThrowsAsync<EndOfStreamException>(() => reader.ReadAsync(CancellationToken.None));
    }
}

public class ValveRconClientTests
{
    private const int Auth = 3, AuthResponse = 2, Exec = 2, Response = 0;

    /// <summary>A Source server: empty RESPONSE_VALUE then AUTH_RESPONSE; mirrors the empty sentinel plus the 0x01 quirk packet.</summary>
    private static IEnumerable<byte[]> SourceServer(RconRequest r, string password, Func<string, string[]> commands)
    {
        switch (r.Type)
        {
            case Auth:
                yield return FakeRconServer.Packet(r.Id, Response, "");
                yield return FakeRconServer.Packet(r.Body == password ? r.Id : -1, AuthResponse, "");
                break;
            case Exec:
                foreach (var part in commands(r.Body))
                    yield return FakeRconServer.Packet(r.Id, Response, part);
                break;
            case Response:
                yield return FakeRconServer.Packet(r.Id, Response, "");
                yield return FakeRconServer.Packet(r.Id, Response, "\u0001");
                break;
        }
    }

    [Fact]
    public async Task Rejected_password_fails_even_though_the_preliminary_response_matches_the_id()
    {
        await using var server = new FakeRconServer(r => SourceServer(r, "right", _ => []));
        using var client = new ValveRconClient(IPAddress.Loopback, server.Port, "wrong");

        Assert.False(await client.ConnectAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendCommandAsync("status"));
    }

    [Fact]
    public async Task Only_the_preliminary_response_is_not_authentication()
    {
        await using var server = new FakeRconServer(r => r.Type == Auth ? [FakeRconServer.Packet(r.Id, Response, "")] : []);
        using var client = new ValveRconClient(IPAddress.Loopback, server.Port, "pw") { ResponseTimeout = TimeSpan.FromMilliseconds(300) };

        Assert.False(await client.ConnectAsync());
    }

    [Fact]
    public async Task Correct_password_authenticates()
    {
        await using var server = new FakeRconServer(r => SourceServer(r, "pw", _ => ["ok"]));
        using var client = new ValveRconClient(IPAddress.Loopback, server.Port, "pw");

        Assert.True(await client.ConnectAsync());
        Assert.Equal("ok", await client.SendCommandAsync("echo"));
    }

    [Fact]
    public async Task Multipart_fragmented_response_is_assembled_and_the_next_command_is_not_polluted()
    {
        var parts = new[] { new string('a', 4000), new string('b', 4000), "tail" };
        await using var server = new FakeRconServer(r => SourceServer(r, "pw", cmd => cmd == "status" ? parts : ["second:" + cmd]));
        using var client = new ValveRconClient(IPAddress.Loopback, server.Port, "pw");

        Assert.True(await client.ConnectAsync());
        Assert.Equal(string.Concat(parts), await client.SendCommandAsync("status"));
        // The quirk packet for the first sentinel is still in flight; it must be skipped, not returned.
        Assert.Equal("second:users", await client.SendCommandAsync("users"));
    }

    [Fact]
    public async Task Server_without_sentinel_echo_falls_back_to_the_idle_timeout()
    {
        await using var server = new FakeRconServer(r => r.Type switch
        {
            Auth => [FakeRconServer.Packet(r.Id, AuthResponse, "")],
            Exec => [FakeRconServer.Packet(r.Id, Response, "one"), FakeRconServer.Packet(r.Id, Response, "two")],
            _ => [],
        });
        using var client = new ValveRconClient(IPAddress.Loopback, server.Port, "pw") { MultiPacketIdleTimeout = TimeSpan.FromMilliseconds(200) };

        Assert.True(await client.ConnectAsync());
        Assert.Equal("onetwo", await client.SendCommandAsync("x"));
    }
}

public class MinecraftRconClientTests
{
    private const int Auth = 3, AuthResponse = 2, Exec = 2, Response = 0;

    private static IEnumerable<byte[]> MinecraftServer(RconRequest r, string password, Func<string, string[]> commands)
    {
        switch (r.Type)
        {
            case Auth:
                yield return FakeRconServer.Packet(r.Body == password ? r.Id : -1, AuthResponse, "");
                break;
            case Exec:
                foreach (var part in commands(r.Body))
                    yield return FakeRconServer.Packet(r.Id, Response, part);
                break;
            default:
                yield return FakeRconServer.Packet(r.Id, Response, $"Unknown request {r.Type:x}");
                break;
        }
    }

    [Fact]
    public async Task Rejected_password_fails()
    {
        await using var server = new FakeRconServer(r => MinecraftServer(r, "right", _ => []));
        using var client = new MinecraftRconClient("127.0.0.1", server.Port, "wrong");
        Assert.False(await client.ConnectAsync());
    }

    [Fact]
    public async Task Multipart_fragmented_response_is_assembled()
    {
        var parts = new[] { new string('x', 4096), new string('y', 4096), "end" };
        await using var server = new FakeRconServer(r => MinecraftServer(r, "pw", cmd => cmd == "list" ? parts : ["pong"]), chunk: 7);
        using var client = new MinecraftRconClient("127.0.0.1", server.Port, "pw");

        Assert.True(await client.ConnectAsync());
        Assert.Equal(string.Concat(parts), await client.SendCommandAsync("list"));
        Assert.Equal("pong", await client.SendCommandAsync("ping"));
        Assert.Contains(server.Requests, r => r.Type == MinecraftRconClient.SentinelPacketType);
    }
}
