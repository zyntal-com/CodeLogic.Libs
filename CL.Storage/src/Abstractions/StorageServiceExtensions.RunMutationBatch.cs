using System.Runtime.CompilerServices;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Abstractions;

public static partial class StorageServiceExtensions
{

    private static async Task RunMutationBatchAsync(
        int count,
        StorageBatchOptions options,
        CancellationToken cancellationToken,
        Func<int, CancellationToken, Task> operation)
    {
        await Parallel.ForEachAsync(
            Enumerable.Range(0, count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = options.MaxConcurrency,
                CancellationToken = cancellationToken
            },
            async (index, token) => await operation(index, token).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static Result ValidateBatch(StorageBatchOptions options, int count)
    {
        var validation = options.Validate();
        if (validation.IsFailure)
            return validation;
        return count <= options.MaxItems
            ? Result.Success()
            : Result.Failure(StorageErrors.TooLarge(
                $"The batch contains {count} items; the configured maximum is {options.MaxItems}."));
    }

    private static HashAlgorithmName ToHashAlgorithmName(StorageChecksumAlgorithm algorithm) => algorithm switch
    {
        StorageChecksumAlgorithm.Md5 => HashAlgorithmName.MD5,
        StorageChecksumAlgorithm.Sha256 => HashAlgorithmName.SHA256,
        StorageChecksumAlgorithm.Sha384 => HashAlgorithmName.SHA384,
        StorageChecksumAlgorithm.Sha512 => HashAlgorithmName.SHA512,
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm))
    };

    private static int? ChecksumLength(StorageChecksumAlgorithm algorithm) => algorithm switch
    {
        StorageChecksumAlgorithm.Md5 => 16,
        StorageChecksumAlgorithm.Sha256 => 32,
        StorageChecksumAlgorithm.Sha384 => 48,
        StorageChecksumAlgorithm.Sha512 => 64,
        _ => null
    };

    private sealed class ProgressReadStream(
        Stream inner,
        IProgress<StorageTransferProgress> progress,
        long? totalBytes,
        bool leaveOpen) : Stream
    {
        private long _bytesTransferred;
        private int _completed;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => totalBytes ?? throw new NotSupportedException();
        public override long Position
        {
            get => Interlocked.Read(ref _bytesTransferred);
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Observe(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Observe(inner.Read(buffer));

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            return Observe(read);
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            return Observe(read);
        }

        private int Observe(int read)
        {
            var transferred = read > 0
                ? Interlocked.Add(ref _bytesTransferred, read)
                : Interlocked.Read(ref _bytesTransferred);
            var complete = read == 0 || (totalBytes.HasValue && transferred >= totalBytes.Value);
            if (!complete || Interlocked.Exchange(ref _completed, 1) == 0)
                progress.Report(new StorageTransferProgress(transferred, totalBytes, complete));
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !leaveOpen)
                inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!leaveOpen)
                await inner.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }

    private sealed class BoundedMemoryStream(int maxBytes) : MemoryStream(Math.Min(maxBytes, 65_536))
    {
        private readonly int _maxBytes = maxBytes;

        public override void SetLength(long value)
        {
            if (value > _maxBytes) throw new StorageBufferLimitException();
            base.SetLength(value);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacityFor(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacityFor(buffer.Length);
            base.Write(buffer);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            EnsureCapacityFor(count);
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            EnsureCapacityFor(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }

        public override void WriteByte(byte value)
        {
            EnsureCapacityFor(1);
            base.WriteByte(value);
        }

        private void EnsureCapacityFor(int count)
        {
            if (count < 0 || Position > _maxBytes - count || Math.Max(Length, Position + count) > _maxBytes)
                throw new StorageBufferLimitException();
        }
    }

    private sealed class StorageBufferLimitException : IOException { }
}
