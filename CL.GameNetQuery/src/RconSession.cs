using System.Buffers.Binary;
using System.Text;

namespace CL.GameNetQuery;

/// <summary>
/// The Source RCON conversation shared by <see cref="Valve.ValveRconClient"/> and
/// <see cref="Minecraft.MinecraftRconClient"/>: exact packet framing, authentication that waits for the
/// real <c>SERVERDATA_AUTH_RESPONSE</c>, and multi-packet command responses terminated by a sentinel request.
/// Not thread-safe on its own; the clients serialize calls.
/// </summary>
internal sealed class RconSession(Stream stream, int sentinelType)
{
    /// <summary><c>SERVERDATA_RESPONSE_VALUE</c>.</summary>
    public const int ResponseValue = 0;
    /// <summary><c>SERVERDATA_AUTH_RESPONSE</c> (server to client) / <c>SERVERDATA_EXECCOMMAND</c> (client to server).</summary>
    public const int AuthResponseOrExec = 2;
    /// <summary><c>SERVERDATA_AUTH</c>.</summary>
    public const int Auth = 3;

    private readonly RconFrameReader _reader = new(stream);
    private int _nextId;

    /// <summary>
    /// Sends the password and waits for the <c>SERVERDATA_AUTH_RESPONSE</c>. Source servers send an empty
    /// <c>SERVERDATA_RESPONSE_VALUE</c> first; it is skipped. The response id must equal the request id;
    /// <c>-1</c> means the password was rejected.
    /// </summary>
    /// <returns><see langword="true"/> only for an auth response that carries the request id.</returns>
    public async Task<bool> AuthenticateAsync(string password, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var id = NextId();
        await stream.WriteAsync(Build(id, Auth, password), cancellationToken).ConfigureAwait(false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        while (true)
        {
            var frame = await _reader.ReadAsync(cts.Token).ConfigureAwait(false);
            if (frame.Type != AuthResponseOrExec)
                continue;                       // the preliminary empty RESPONSE_VALUE, or a stale packet
            if (frame.Id == id)
                return true;
            if (frame.Id == -1)
                return false;                   // wrong password
        }
    }

    /// <summary>
    /// Runs <paramref name="command"/> and returns every response body for it, concatenated in order.
    /// The command is followed by a sentinel packet; the server answers requests in order, so the reply to
    /// the sentinel marks the end of the command's (possibly multi-packet) response. Packets with other ids
    /// (late replies to earlier, timed-out requests) are ignored.
    /// </summary>
    /// <param name="command">The console command.</param>
    /// <param name="timeout">Overall time allowed for the response.</param>
    /// <param name="idleTimeout">
    /// For servers that do not answer the sentinel: once at least one body has arrived, a gap this long ends the response.
    /// </param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    public async Task<string> ExecuteAsync(string command, TimeSpan timeout, TimeSpan idleTimeout, CancellationToken cancellationToken = default)
    {
        var id = NextId();
        var sentinelId = NextId();
        var request = Build(id, AuthResponseOrExec, command);
        var sentinel = Build(sentinelId, sentinelType, string.Empty);
        var both = new byte[request.Length + sentinel.Length];
        request.CopyTo(both, 0);
        sentinel.CopyTo(both, request.Length);
        await stream.WriteAsync(both, cancellationToken).ConfigureAwait(false);

        var body = new StringBuilder();
        var gotBody = false;
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overall.CancelAfter(timeout);

        while (true)
        {
            RconFrame frame;
            using (var step = CancellationTokenSource.CreateLinkedTokenSource(overall.Token))
            {
                if (gotBody)
                    step.CancelAfter(idleTimeout);
                try
                {
                    frame = await _reader.ReadAsync(step.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (gotBody && !cancellationToken.IsCancellationRequested)
                {
                    break;                      // no sentinel reply: return what arrived
                }
            }

            if (frame.Id == sentinelId)
                break;                          // the reply to the sentinel: the response is complete
            if (frame.Id == id && frame.Type == ResponseValue)
            {
                body.Append(DecodeBody(frame.Packet));
                gotBody = true;
            }
        }

        return body.ToString();
    }

    /// <summary>Serializes one packet: size, id, type, UTF-8 body, two null terminators.</summary>
    public static byte[] Build(int id, int type, string body)
    {
        var bodyLength = Encoding.UTF8.GetByteCount(body);
        var packet = new byte[4 + 4 + 4 + bodyLength + 2];
        BinaryPrimitives.WriteInt32LittleEndian(packet, packet.Length - 4);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), id);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), type);
        Encoding.UTF8.GetBytes(body, packet.AsSpan(12));
        return packet;
    }

    /// <summary>Decodes a packet's body (between the header and the trailing null terminators).</summary>
    public static string DecodeBody(byte[] packet)
    {
        var body = packet.AsSpan(12);
        var end = body.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? body : body[..end]);
    }

    private int NextId()
    {
        // Positive ids only: -1 is the server's "authentication failed" marker.
        var id = Interlocked.Increment(ref _nextId) & int.MaxValue;
        return id == 0 ? NextId() : id;
    }
}
