using System.Net;
using System.Net.Sockets;

namespace CL.GameNetQuery.Valve;

/// <summary>
/// RCON client for Valve Source Engine servers (CSS, CS2).
/// Handles authentication, command execution, and response parsing.
/// </summary>
public sealed class ValveRconClient : IDisposable
{
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

    /// <summary>Initializes a new RCON client using a string IP address.</summary>
    /// <param name="ip">The server IP address as a string.</param>
    /// <param name="port">The RCON port number.</param>
    /// <param name="password">The RCON password.</param>
    public ValveRconClient(string ip, ushort port, string password)
    {
        _serverIp = IPAddress.Parse(ip);
        _port = port;
        _password = password;
    }

    /// <summary>Initializes a new RCON client using an <see cref="IPAddress"/>.</summary>
    /// <param name="ip">The server IP address.</param>
    /// <param name="port">The RCON port number.</param>
    /// <param name="password">The RCON password.</param>
    public ValveRconClient(IPAddress ip, ushort port, string password)
    {
        _serverIp = ip;
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
            var session = new RconSession(_networkStream, sentinelType: RconSession.ResponseValue);
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
