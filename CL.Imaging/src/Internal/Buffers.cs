using System.Buffers;
using CL.Imaging.Errors;
using CodeLogic.Core.Results;

namespace CL.Imaging.Internal;

/// <summary>A rented array and the number of bytes used in it. Dispose returns the array to the pool.</summary>
internal sealed class PooledBuffer : IDisposable
{
    private byte[]? _array;

    public PooledBuffer(byte[] array, int length)
    {
        _array = array;
        Length = length;
    }

    public int Length { get; }

    public byte[] Array => _array ?? throw new ObjectDisposedException(nameof(PooledBuffer));

    public ReadOnlySpan<byte> Span => Array.AsSpan(0, Length);

    public ReadOnlyMemory<byte> Memory => Array.AsMemory(0, Length);

    public void Dispose()
    {
        var array = Interlocked.Exchange(ref _array, null);
        if (array is not null) ArrayPool<byte>.Shared.Return(array);
    }
}

/// <summary>A growable write-only stream backed by <see cref="ArrayPool{T}"/>; <see cref="Detach"/> hands the buffer over.</summary>
internal sealed class PooledMemoryStream : Stream
{
    private byte[] _buffer;
    private int _length;

    public PooledMemoryStream(int initialCapacity = 64 * 1024) => _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(256, initialCapacity));

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _length;
    public override long Position { get => _length; set => throw new NotSupportedException(); }

    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _length);

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Ensure(_length + buffer.Length);
        buffer.CopyTo(_buffer.AsSpan(_length));
        _length += buffer.Length;
    }

    public override void WriteByte(byte value)
    {
        Ensure(_length + 1);
        _buffer[_length++] = value;
    }

    private void Ensure(int needed)
    {
        if (needed <= _buffer.Length) return;
        var grown = ArrayPool<byte>.Shared.Rent(Math.Max(needed, _buffer.Length * 2));
        _buffer.AsSpan(0, _length).CopyTo(grown);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = grown;
    }

    /// <summary>Hands the written bytes over as a <see cref="PooledBuffer"/>; the stream is empty afterwards.</summary>
    public PooledBuffer Detach()
    {
        var result = new PooledBuffer(_buffer, _length);
        _buffer = [];
        _length = 0;
        return result;
    }

    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_buffer.Length > 0) ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
        _length = 0;
        base.Dispose(disposing);
    }
}

/// <summary>Reads whole streams into pooled buffers, enforcing a byte limit.</summary>
internal static class StreamBuffer
{
    public static Result<PooledBuffer> ReadAll(Stream stream, long maxBytes)
    {
        if (TooLong(stream, maxBytes) is { } tooLong) return tooLong;
        var buffer = ArrayPool<byte>.Shared.Rent(InitialSize(stream, maxBytes));
        var length = 0;
        try
        {
            while (true)
            {
                if (length == buffer.Length) buffer = Grow(buffer, length);
                var read = stream.Read(buffer, length, buffer.Length - length);
                if (read == 0) break;
                length += read;
                if (maxBytes > 0 && length > maxBytes)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    return OverLimit(maxBytes);
                }
            }
            return new PooledBuffer(buffer, length);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    public static async ValueTask<Result<PooledBuffer>> ReadAllAsync(Stream stream, long maxBytes, CancellationToken ct)
    {
        if (TooLong(stream, maxBytes) is { } tooLong) return tooLong;
        var buffer = ArrayPool<byte>.Shared.Rent(InitialSize(stream, maxBytes));
        var length = 0;
        try
        {
            while (true)
            {
                if (length == buffer.Length) buffer = Grow(buffer, length);
                var read = await stream.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false);
                if (read == 0) break;
                length += read;
                if (maxBytes > 0 && length > maxBytes)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    return OverLimit(maxBytes);
                }
            }
            return new PooledBuffer(buffer, length);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    private static Error? TooLong(Stream stream, long maxBytes)
    {
        if (maxBytes <= 0 || !stream.CanSeek) return null;
        var remaining = stream.Length - stream.Position;
        return remaining > maxBytes ? OverLimit(maxBytes) : null;
    }

    private static Error OverLimit(long maxBytes) =>
        ImagingErrors.TooLarge($"The image data is larger than the limit of {maxBytes:N0} bytes.");

    /// <summary>The most rented up front when no byte limit applies; a larger stream grows the buffer as it is read.</summary>
    internal const int MaxInitialRent = 16 * 1024 * 1024;

    /// <summary>
    /// The first buffer size: what a seekable stream says remains (plus one byte to see the end), but never more than
    /// the limit allows, and without a limit never more than <see cref="MaxInitialRent"/>, so a stream that reports a
    /// huge length does not make it rent that much before a byte is read.
    /// </summary>
    internal static int InitialSize(Stream stream, long maxBytes)
    {
        var cap = maxBytes > 0 ? Math.Min(maxBytes + 1, Array.MaxLength) : MaxInitialRent;
        if (!stream.CanSeek) return (int)Math.Clamp(64 * 1024, 256, Math.Max(256, cap));
        var remaining = stream.Length - stream.Position;
        return (int)Math.Clamp(remaining + 1, 256, Math.Max(256, cap));
    }

    private static byte[] Grow(byte[] buffer, int length)
    {
        var grown = ArrayPool<byte>.Shared.Rent((int)Math.Min((long)buffer.Length * 2, Array.MaxLength));
        buffer.AsSpan(0, length).CopyTo(grown);
        ArrayPool<byte>.Shared.Return(buffer);
        return grown;
    }
}
