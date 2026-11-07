using System.Buffers;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Registry;

internal static partial class StagedWriter
{

    private static async Task<Result<long>> ProduceAsync(
        Stream source,
        PipeWriter writer,
        CancellationToken relayCancellationToken,
        CancellationToken callerCancellationToken)
    {
        long bytes = 0;
        Exception? completionError = null;
        try
        {
            while (true)
            {
                var memory = writer.GetMemory(SegmentSize)[..SegmentSize];
                var read = await source.ReadAsync(memory, relayCancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                writer.Advance(read);
                bytes = checked(bytes + read);
                var flush = await writer.FlushAsync(relayCancellationToken).ConfigureAwait(false);
                if (flush.IsCanceled)
                    relayCancellationToken.ThrowIfCancellationRequested();
                if (flush.IsCompleted)
                    return Result<long>.Failure(StorageErrors.Unavailable(
                        "The transfer destination stopped reading before the source reached end-of-stream."));
            }
            return Result<long>.Success(bytes);
        }
        catch (OperationCanceledException error) when (callerCancellationToken.IsCancellationRequested)
        {
            completionError = error;
            throw;
        }
        catch (OperationCanceledException error)
        {
            completionError = error;
            return Result<long>.Failure(StorageErrors.Unavailable(
                "The transfer stream stopped because the peer operation did not complete."));
        }
        catch (SourceTooLongException error)
        {
            completionError = error;
            return Result<long>.Failure(StorageErrors.Conflict("The source delivered more bytes than expected."));
        }
        catch (Exception error)
        {
            completionError = error;
            return Result<long>.Failure(StorageErrors.FromException(error, "Read transfer source"));
        }
        finally
        {
            await writer.CompleteAsync(completionError).ConfigureAwait(false);
        }
    }

    private static async Task ObserveProducerAsync(Task<Result<long>> producer)
    {
        try { _ = await producer.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch { }
    }

    internal static string Parent(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? string.Empty : path[..index];
    }

    internal static string Combine(string parent, string child) =>
        parent.Length == 0 ? child.TrimStart('/') : $"{parent.TrimEnd('/')}/{child.TrimStart('/')}";

    /// <summary>Raised when a source delivers more than its expected length.</summary>
    private sealed class SourceTooLongException() : IOException("The source delivered more bytes than expected.");

    /// <summary>Counts, hashes, and caps what is read from a source.</summary>
    private sealed class GuardedReadStream(Stream inner, IncrementalHash? hash, long? limit) : Stream
    {
        private long _read;

        public long BytesRead => Interlocked.Read(ref _read);
        public bool Exceeded { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Account(buffer.AsSpan(offset, inner.Read(buffer, offset, count)));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            return Account(buffer.Span[..count]);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private int Account(ReadOnlySpan<byte> data)
        {
            if (data.Length == 0) return 0;
            var total = Interlocked.Add(ref _read, data.Length);
            if (limit is { } max && total > max)
            {
                // Probing one byte past the expected end is how a longer source is detected.
                Exceeded = true;
                throw new SourceTooLongException();
            }
            hash?.AppendData(data);
            return data.Length;
        }
    }

    private sealed class CountingReadStream(Stream inner) : Stream
    {
        private long _bytesRead;

        internal long BytesRead => Interlocked.Read(ref _bytesRead);
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Interlocked.Add(ref _bytesRead, read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            Interlocked.Add(ref _bytesRead, read);
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            Interlocked.Add(ref _bytesRead, read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Interlocked.Add(ref _bytesRead, read);
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }
}
