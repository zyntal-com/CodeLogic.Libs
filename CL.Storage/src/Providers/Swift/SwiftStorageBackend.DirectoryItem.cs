using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CodeLogic.Core.Results;

namespace CL.Storage.Providers.Swift;

public sealed partial class SwiftStorageBackend
{

    private static StorageItem DirectoryItem(string path) => new()
    {
        Path = path,
        Name = path.Length == 0 ? string.Empty : NameOf(path),
        ItemType = StorageItemType.Directory
    };

    private static string NameOf(string path) => path.Split('/')[^1];

    private static Error FromStatus(HttpResponseMessage response, string operation) =>
        ProviderErrorMapper.FromHttpStatus(
            (int)response.StatusCode,
            operation,
            "Swift",
            ProviderErrorMapper.RetryAfter(response.Headers.RetryAfter));

    private static Error Map(Exception exception, string operation)
    {
        if (exception is HttpRequestException { StatusCode: { } status })
            return ProviderErrorMapper.FromHttpStatus((int)status, operation, "Swift");
        return ProviderErrorMapper.FromTransport(exception, operation, "Swift")
            ?? StorageErrors.ProviderError($"{operation}: Swift provider failed.", ProviderErrorMapper.ExceptionDetails(exception));
    }

    private sealed record SwiftPage(IReadOnlyList<SwiftListItem> Items, string? NextMarker);
    private sealed record SwiftListItem(
        string? Name,
        string? Subdirectory,
        long? Bytes,
        DateTimeOffset? LastModified,
        string? ContentType,
        string? Hash);

    private sealed class NonDisposingReadStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { }
    }
}
