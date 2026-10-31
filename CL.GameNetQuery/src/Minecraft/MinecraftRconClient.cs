using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CL.GameNetQuery.Minecraft;

/// <summary>
/// RCON client for Minecraft servers.
/// Uses the same RCON protocol as Valve but with Minecraft-specific packet handling.
/// </summary>
public sealed class MinecraftRconClient : IDisposable
{
    /// <summary>
    /// Type of the end-of-response marker packet. Minecraft answers a request of an unknown type with an
    /// "Unknown request" response carrying the same id, after any earlier response.
    /// </summary>
    internal const int SentinelPacketType = 200;

    private readonly IPAddress _serverIp;
    private readonly ushort _port;
    private readonly string _password;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TcpClient? _tcpClient;
    private NetworkStream? _networkStream;
    private RconSession? _session;

    /// <summary>Time allowed for the server's reply to authentication or a command. Default 5 seconds.</summary>
    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Fallback for servers that do not answer the end-of-response marker: once part of a command's response has
    /// arrived, a pause this long ends it. Default 500 ms.
    /// </summary>
    public TimeSpan MultiPacketIdleTimeout { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Initializes a new Minecraft RCON client with the specified connection details.</summary>
    /// <param name="ip">The server IP address as a string.</param>
    /// <param name="port">The RCON port number.</param>
    /// <param name="password">The RCON password.</param>
    public MinecraftRconClient(string ip, ushort port, string password)
    {
        _serverIp = IPAddress.Parse(ip);
        _port = port;
        _password = password;
    }

    /// <summary>
    /// Connects to the RCON server and authenticates.
    /// </summary>
    /// <param name="timeoutMs">Connection timeout in milliseconds.</param>
    /// <returns>
    /// <c>true</c> only when the server sent an auth response (<c>SERVERDATA_AUTH_RESPONSE</c>) carrying the
    /// request id; <c>false</c> on a connection failure, a rejected password (response id <c>-1</c>) or no
    /// auth response within <see cref="ResponseTimeout"/>.
    /// </returns>
    public async Task<bool> ConnectAsync(int timeoutMs = 5000)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            CloseConnection();
            _tcpClient = new TcpClient(_serverIp.AddressFamily) { NoDelay = true };
            using (var cts = new CancellationTokenSource(timeoutMs))
                await _tcpClient.ConnectAsync(_serverIp, _port, cts.Token).ConfigureAwait(false);
            _networkStream = _tcpClient.GetStream();
            var session = new RconSession(_networkStream, SentinelPacketType);
            if (!await session.AuthenticateAsync(_password, ResponseTimeout).ConfigureAwait(false))
            {
                CloseConnection();
                return false;
            }
            _session = session;
            return true;
        }
        catch
        {
            CloseConnection();
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Sends a command and returns the response.
    /// </summary>
    /// <param name="command">The command to execute.</param>
    /// <returns>
    /// The server response; a response split over several packets is joined in order. Empty when the
    /// server did not answer within <see cref="ResponseTimeout"/> or the connection failed (the client
    /// then disconnects).
    /// </returns>
    /// <exception cref="InvalidOperationException">The client is not connected and authenticated.</exception>
    public async Task<string> SendCommandAsync(string command)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var session = _session ?? throw new InvalidOperationException("Not connected to RCON server.");
            try
            {
                return await session.ExecuteAsync(command, ResponseTimeout, MultiPacketIdleTimeout).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return string.Empty;            // no reply in time; late packets are skipped by id
            }
            catch
            {
                CloseConnection();              // closed or out of sync: the connection is unusable
                return string.Empty;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Disconnects from the RCON server.
    /// </summary>
    public void Disconnect() => CloseConnection();

    private void CloseConnection()
    {
        _session = null;
        _networkStream?.Close();
        _tcpClient?.Close();
        _networkStream = null;
        _tcpClient = null;
    }

    /// <summary>Disposes the client by disconnecting from the server.</summary>
    public void Dispose() => Disconnect();
}

/// <summary>
/// Packet types used in Minecraft RCON communication.
/// </summary>
public enum MinecraftPacketType
{
    /// <summary>A response packet from the server.</summary>
    Response = 0,
    /// <summary>An authentication response packet.</summary>
    AuthResponse = 2,
    /// <summary>A command execution request packet.</summary>
    ExecCommand = 2,
    /// <summary>An authentication request packet.</summary>
    Auth = 3
}

/// <summary>
/// Represents a packet in the Minecraft RCON protocol.
/// </summary>
public sealed class MinecraftRconPacket
{
    private MinecraftRconPacket() { }

    /// <summary>Gets the packet identifier.</summary>
    public int Id { get; private init; }
    /// <summary>Gets the packet type.</summary>
    public MinecraftPacketType Type { get; private init; }
    /// <summary>Gets the packet body content.</summary>
    public string Body { get; private init; } = string.Empty;

    /// <summary>Creates a new Minecraft RCON packet with the specified id, type, and content.</summary>
    /// <param name="id">The packet identifier.</param>
    /// <param name="type">The packet type.</param>
    /// <param name="content">The packet body content.</param>
    /// <returns>A new <see cref="MinecraftRconPacket"/> instance.</returns>
    public static MinecraftRconPacket Create(int id, MinecraftPacketType type, string content) => new()
    {
        Type = type,
        Body = string.IsNullOrEmpty(content) ? "\0" : content,
        Id = id
    };

    /// <summary>Serializes this packet to a byte array for transmission.</summary>
    /// <returns>The serialized packet bytes.</returns>
    public byte[] ToBytes()
    {
        var body = Encoding.ASCII.GetBytes(Body + "\0");
        var buffer = new byte[12 + body.Length + 1];
        var span = buffer.AsSpan();
        body.CopyTo(span[12..]);
        BitConverter.GetBytes(body.Length + 9).CopyTo(span[0..4]);
        BitConverter.GetBytes(Id).CopyTo(span[4..8]);
        BitConverter.GetBytes((int)Type).CopyTo(span[8..12]);
        return buffer;
    }

    /// <summary>Deserializes a Minecraft RCON packet from a byte array.</summary>
    /// <param name="buffer">The raw packet bytes.</param>
    /// <returns>The deserialized <see cref="MinecraftRconPacket"/>.</returns>
    public static MinecraftRconPacket FromBytes(byte[] buffer) => new()
    {
        Type = (MinecraftPacketType)BitConverter.ToInt32(buffer[8..12]),
        Body = Encoding.ASCII.GetString(buffer[12..]).Replace("\0", ""),
        Id = BitConverter.ToInt32(buffer[4..8])
    };
}
