namespace CL.NetUtils.Services;

/// <summary>Thrown by <see cref="BoundedReadStream"/> once more than its limit has been read.</summary>
internal sealed class DownloadTooLargeException(string message) : IOException(message);

/// <summary>
/// A read-only pass-through that counts the bytes read from <paramref name="inner"/> and throws
/// <see cref="DownloadTooLargeException"/> as soon as they exceed <paramref name="maxBytes"/>. Put it on an HTTP body
/// whose length the server did not state, and on a decompressor's output, so neither a long body nor a small archive
/// that inflates to gigabytes can fill the disk.
/// </summary>
internal sealed class BoundedReadStream(Stream inner, long maxBytes, string what, CancellationToken cancellationToken = default) : Stream
{
    private long _read;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Count(inner.Read(buffer));
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private int Count(int read)
    {
        _read += read;
        if (_read > maxBytes)
            throw new DownloadTooLargeException($"The {what} exceeds the limit of {maxBytes:N0} bytes (GeoIp.MaxDownloadBytes).");
        return read;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
