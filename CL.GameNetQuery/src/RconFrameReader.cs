using System.Buffers.Binary;

namespace CL.GameNetQuery;

/// <summary>One RCON packet as it arrived on the wire: the size field is consumed, the rest is kept raw.</summary>
/// <param name="Id">The request id the packet answers (<c>-1</c> for a failed authentication).</param>
/// <param name="Type">The raw packet type.</param>
/// <param name="Packet">The full packet including the 4-byte size prefix, in the shape <c>FromBytes</c> expects.</param>
internal readonly record struct RconFrame(int Id, int Type, byte[] Packet);

/// <summary>
/// Reads length-prefixed Source/Minecraft RCON packets from a stream.
/// Bytes are kept in a persistent buffer across calls, so a packet split over several TCP segments
/// (or several packets in one segment) is reassembled exactly, and a read that is cancelled by a
/// timeout loses no data: the partial packet stays buffered for the next call.
/// </summary>
internal sealed class RconFrameReader(Stream stream)
{
    /// <summary>Smallest legal size field: id (4) + type (4) + two null terminators.</summary>
    public const int MinPacketSize = 10;

    /// <summary>Largest accepted size field. Servers send at most 4 KiB per packet; anything far larger is a framing error.</summary>
    public const int MaxPacketSize = 64 * 1024;

    private byte[] _buffer = new byte[8192];
    private int _count;

    /// <summary>Reads the next complete packet.</summary>
    /// <exception cref="EndOfStreamException">The connection closed before a complete packet arrived.</exception>
    /// <exception cref="InvalidDataException">The size field is out of range (the stream is out of sync).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> fired; buffered bytes are kept.</exception>
    public async Task<RconFrame> ReadAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (TryTake(out var frame))
                return frame;

            if (_count == _buffer.Length)
                Array.Resize(ref _buffer, _buffer.Length * 2);

            var read = await stream.ReadAsync(_buffer.AsMemory(_count), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("The RCON connection was closed.");
            _count += read;
        }
    }

    private bool TryTake(out RconFrame frame)
    {
        frame = default;
        if (_count < 4)
            return false;

        var size = BinaryPrimitives.ReadInt32LittleEndian(_buffer);
        if (size is < MinPacketSize or > MaxPacketSize)
            throw new InvalidDataException($"Invalid RCON packet size {size}.");

        var total = size + 4;
        if (_count < total)
            return false;

        var packet = _buffer.AsSpan(0, total).ToArray();
        _buffer.AsSpan(total, _count - total).CopyTo(_buffer);
        _count -= total;

        frame = new RconFrame(
            BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(4)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(8)),
            packet);
        return true;
    }
}
