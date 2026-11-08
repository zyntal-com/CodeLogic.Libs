using System.Net;
using System.Reflection;
using Amazon.S3;
using Amazon.S3.Model;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Providers.S3;
using Xunit;

namespace Storage.Tests;

public sealed class S3StorageBackendTests
{
    private const int PartSize = 5 * 1024 * 1024;

    [Fact]
    public async Task Multipart_upload_is_bounded_supports_nonseekable_input_and_atomically_creates()
    {
        var client = DispatchProxy.Create<IAmazonS3, RecordingS3ClientProxy>();
        var recorder = (RecordingS3ClientProxy)(object)client;
        await using var backend = new S3StorageBackend(
            "S3",
            client,
            "bucket",
            prefix: "mounted",
            multipartPartSizeBytes: PartSize,
            multipartThresholdBytes: PartSize)
        {
            // Forwarding the headers is the Enforced behaviour; under Auto they are sent only after the probe.
            ConditionalRequests = S3ConditionalRequestSupport.Enforced
        };
        var content = Enumerable.Range(0, PartSize + 17).Select(index => (byte)(index % 251)).ToArray();
        await using var source = new TrackingNonSeekableStream(content);

        var result = await backend.UploadAsync("folder/large.bin", source, new StorageUploadOptions
        {
            Overwrite = false,
            ContentType = "application/octet-stream",
            Metadata = new Dictionary<string, string> { ["color"] = "blue" }
        });

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.False(source.Disposed);
        Assert.Equal([PartSize, 17], recorder.PartLengths);
        Assert.Equal(content, recorder.UploadedBytes.ToArray());
        Assert.Equal("*", recorder.CompletedRequest!.IfNoneMatch);
        Assert.Equal("mounted/folder/large.bin", recorder.CompletedRequest.Key);
        Assert.Equal("application/octet-stream", recorder.InitiatedRequest!.ContentType);
        Assert.Equal("blue", recorder.InitiatedRequest.Metadata["color"]);
        Assert.False(recorder.AbortCalled);
        Assert.True(backend.Capabilities.Supports(StorageFeature.MultipartUpload));
        Assert.True(backend.Capabilities.Supports(StorageFeature.ConditionalCreate));
    }

    [Fact]
    public async Task Multipart_failure_aborts_upload_and_does_not_dispose_caller_stream()
    {
        var client = DispatchProxy.Create<IAmazonS3, RecordingS3ClientProxy>();
        var recorder = (RecordingS3ClientProxy)(object)client;
        recorder.FailPartNumber = 2;
        await using var backend = new S3StorageBackend(
            "S3",
            client,
            "bucket",
            multipartPartSizeBytes: PartSize,
            multipartThresholdBytes: PartSize);
        await using var source = new TrackingNonSeekableStream(new byte[PartSize + 1]);

        var result = await backend.UploadAsync("large.bin", source);

        Assert.True(result.IsFailure);
        Assert.Equal(StorageErrors.ServerBusyCode, result.Error!.Code);
        Assert.True(StorageErrorInfo.IsTransient(result.Error));
        Assert.True(recorder.AbortCalled);
        Assert.False(source.Disposed);
        Assert.Null(recorder.CompletedRequest);
    }

    [Fact]
    public async Task Signed_urls_and_version_downloads_forward_root_scoped_provider_options()
    {
        var client = DispatchProxy.Create<IAmazonS3, RecordingS3ClientProxy>();
        var recorder = (RecordingS3ClientProxy)(object)client;
        await using var backend = new S3StorageBackend("S3", client, "bucket", prefix: "root");

        var signed = await backend.CreateSignedUrlAsync(@"folder\item.bin", new StorageSignedUrlOptions
        {
            Method = StorageSignedUrlMethod.Read,
            VersionId = "version-7",
            ExpiresIn = TimeSpan.FromMinutes(5)
        });
        var download = await backend.DownloadAsync("folder/item.bin", new StorageDownloadOptions
        {
            VersionId = "version-7",
            Offset = 2,
            Length = 3
        });
        await using var stream = download.Value!;

        Assert.True(signed.IsSuccess, signed.Error?.Message);
        Assert.True(download.IsSuccess, download.Error?.Message);
        Assert.Equal("bucket", recorder.PreSignedRequest!.BucketName);
        Assert.Equal("root/folder/item.bin", recorder.PreSignedRequest.Key);
        Assert.Equal("version-7", recorder.PreSignedRequest.VersionId);
        Assert.Equal("version-7", recorder.GetObjectRequest!.VersionId);
        Assert.Equal("bytes=2-4", recorder.GetObjectRequest.ByteRange.FormattedByteRange);
        Assert.True(backend.Capabilities.Supports(StorageFeature.SignedReadUrls));
        Assert.True(backend.Capabilities.Supports(StorageFeature.Versioning));
    }

    [Fact]
    public async Task Version_listing_and_deletion_are_exact_paged_and_root_scoped()
    {
        var client = DispatchProxy.Create<IAmazonS3, RecordingS3ClientProxy>();
        var recorder = (RecordingS3ClientProxy)(object)client;
        recorder.ListVersionsResponse = new ListVersionsResponse
        {
            Versions =
            [
                new S3ObjectVersion
                {
                    Key = "root/folder/item.bin",
                    VersionId = "version-2",
                    ETag = "\"etag-2\"",
                    Size = 7,
                    IsLatest = true
                },
                new S3ObjectVersion
                {
                    Key = "root/folder/item.bin-other",
                    VersionId = "unrelated"
                }
            ],
            IsTruncated = true,
            NextKeyMarker = "root/folder/item.bin",
            NextVersionIdMarker = "version-1"
        };
        await using var backend = new S3StorageBackend("S3", client, "bucket", prefix: "root");

        var listed = await backend.ListVersionsAsync("folder/item.bin", new StorageVersionListOptions
        {
            PageSize = 25
        });
        var deleted = await backend.DeleteVersionAsync("folder/item.bin", "version-2");

        Assert.True(listed.IsSuccess, listed.Error?.Message);
        var version = Assert.Single(listed.Value!.Versions);
        Assert.Equal("folder/item.bin", version.Path);
        Assert.Equal("version-2", version.VersionId);
        Assert.Equal("etag-2", version.ETag);
        Assert.True(version.IsLatest);
        Assert.NotNull(listed.Value.ContinuationToken);
        Assert.Equal("root/folder/item.bin", recorder.ListVersionsRequest!.Prefix);
        Assert.Equal(25, recorder.ListVersionsRequest.MaxKeys);
        Assert.True(deleted.IsSuccess, deleted.Error?.Message);
        Assert.Equal("root/folder/item.bin", recorder.DeleteObjectRequest!.Key);
        Assert.Equal("version-2", recorder.DeleteObjectRequest.VersionId);
    }

    [Fact]
    public async Task Object_tags_are_root_scoped_bounded_and_support_merge_updates()
    {
        var client = DispatchProxy.Create<IAmazonS3, RecordingS3ClientProxy>();
        var recorder = (RecordingS3ClientProxy)(object)client;
        recorder.GetObjectTaggingResponse = new GetObjectTaggingResponse
        {
            Tagging =
            [
                new Amazon.S3.Model.Tag { Key = "owner", Value = "storage" }
            ]
        };
        await using var backend = new S3StorageBackend("S3", client, "bucket", prefix: "root");

        var existing = await backend.GetTagsAsync(@"folder\item.bin");
        var updated = await backend.SetTagsAsync(
            "folder/item.bin",
            new Dictionary<string, string> { ["tier"] = "archive" },
            new StorageTagUpdateOptions { Mode = StorageTagUpdateMode.Merge });

        Assert.True(existing.IsSuccess, existing.Error?.Message);
        Assert.Equal("storage", existing.Value!["owner"]);
        Assert.True(updated.IsSuccess, updated.Error?.Message);
        Assert.Equal("root/folder/item.bin", recorder.GetObjectTaggingRequest!.Key);
        Assert.Equal("root/folder/item.bin", recorder.PutObjectTaggingRequest!.Key);
        Assert.Equal(
            new[] { "owner=storage", "tier=archive" },
            recorder.PutObjectTaggingRequest.Tagging.TagSet
                .OrderBy(tag => tag.Key, StringComparer.Ordinal)
                .Select(tag => $"{tag.Key}={tag.Value}"));
        Assert.True(backend.Capabilities.Supports(StorageFeature.Tags));
        Assert.Equal(10, backend.Capabilities.Limits.MaxTags);
    }

    [Fact]
    public async Task Root_no_ops_honor_pre_cancellation_before_contacting_s3()
    {
        var client = DispatchProxy.Create<IAmazonS3, RecordingS3ClientProxy>();
        await using var backend = new S3StorageBackend("S3", client, "bucket");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            backend.GetInfoAsync(string.Empty, cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            backend.CreateDirectoryAsync(string.Empty, cancellation.Token));
    }

    [Fact]
    public async Task Conditional_upload_and_delete_forward_atomic_identity_guards()
    {
        var client = DispatchProxy.Create<IAmazonS3, RecordingS3ClientProxy>();
        var recorder = (RecordingS3ClientProxy)(object)client;
        recorder.MetadataResponse = new GetObjectMetadataResponse
        {
            ETag = "\"etag-current\"",
            VersionId = "version-current",
            ContentLength = 1
        };
        // Forwarding the guards is the Enforced behaviour; under Auto they are sent only after the probe.
        await using var backend = new S3StorageBackend("S3", client, "bucket") { ConditionalRequests = S3ConditionalRequestSupport.Enforced };

        var uploaded = await backend.UploadBytesAsync("item.bin", [1], new StorageUploadOptions
        {
            Condition = new StorageMutationCondition { ExpectedETag = "etag-current" }
        });
        var deleted = await backend.DeleteAsync("item.bin", new StorageDeleteOptions
        {
            Condition = new StorageMutationCondition { ExpectedVersionId = "version-current" }
        });

        Assert.True(uploaded.IsSuccess, uploaded.Error?.Message);
        Assert.Equal("etag-current", recorder.PutObjectRequest!.IfMatch);
        Assert.True(deleted.IsSuccess, deleted.Error?.Message);
        Assert.Equal("etag-current", recorder.DeleteObjectRequest!.IfMatch);
        Assert.True(backend.Capabilities.Supports(StorageFeature.ConditionalDelete));
    }

    [Fact]
    public async Task Listing_an_exact_object_path_returns_conflict_instead_of_an_empty_directory_page()
    {
        var client = DispatchProxy.Create<IAmazonS3, RecordingS3ClientProxy>();
        var recorder = (RecordingS3ClientProxy)(object)client;
        recorder.MetadataResponse = new GetObjectMetadataResponse
        {
            ETag = "\"etag\"",
            ContentLength = 1
        };
        await using var backend = new S3StorageBackend("S3", client, "bucket");

        var result = await backend.ListAsync("item.bin");

        Assert.True(result.IsFailure);
        Assert.Equal(StorageErrors.ConflictCode, result.Error!.Code);
    }

    private sealed class TrackingNonSeekableStream(byte[] content) : Stream
    {
        private readonly MemoryStream _inner = new(content, writable: false);
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}

public class RecordingS3ClientProxy : DispatchProxy
{
    public InitiateMultipartUploadRequest? InitiatedRequest { get; private set; }
    public CompleteMultipartUploadRequest? CompletedRequest { get; private set; }
    public GetPreSignedUrlRequest? PreSignedRequest { get; private set; }
    public GetObjectRequest? GetObjectRequest { get; private set; }
    public ListVersionsRequest? ListVersionsRequest { get; private set; }
    public ListVersionsResponse ListVersionsResponse { get; set; } = new();
    public DeleteObjectRequest? DeleteObjectRequest { get; private set; }
    public PutObjectRequest? PutObjectRequest { get; private set; }
    public GetObjectTaggingRequest? GetObjectTaggingRequest { get; private set; }
    public GetObjectTaggingResponse GetObjectTaggingResponse { get; set; } = new();
    public PutObjectTaggingRequest? PutObjectTaggingRequest { get; private set; }
    public GetObjectMetadataResponse MetadataResponse { get; set; } = new()
    {
        ETag = "\"etag\"",
        ContentLength = 0
    };
    public List<int> PartLengths { get; } = [];
    public MemoryStream UploadedBytes { get; } = new();
    public int? FailPartNumber { get; set; }
    public bool AbortCalled { get; private set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        args ??= [];
        return targetMethod.Name switch
        {
            nameof(IAmazonS3.InitiateMultipartUploadAsync) => Initiate(Require<InitiateMultipartUploadRequest>(args, 0)),
            nameof(IAmazonS3.UploadPartAsync) => UploadPartAsync(
                Require<UploadPartRequest>(args, 0),
                args[1] is CancellationToken cancellationToken ? cancellationToken : default),
            nameof(IAmazonS3.CompleteMultipartUploadAsync) => Complete(Require<CompleteMultipartUploadRequest>(args, 0)),
            nameof(IAmazonS3.AbortMultipartUploadAsync) => Abort(),
            nameof(IAmazonS3.GetPreSignedURLAsync) => Sign(Require<GetPreSignedUrlRequest>(args, 0)),
            nameof(IAmazonS3.GetObjectAsync) => GetObject(Require<GetObjectRequest>(args, 0)),
            nameof(IAmazonS3.ListVersionsAsync) => ListVersions(Require<ListVersionsRequest>(args, 0)),
            nameof(IAmazonS3.DeleteObjectAsync) => DeleteObject(Require<DeleteObjectRequest>(args, 0)),
            nameof(IAmazonS3.PutObjectAsync) => PutObject(Require<PutObjectRequest>(args, 0)),
            nameof(IAmazonS3.GetObjectTaggingAsync) => GetObjectTagging(Require<GetObjectTaggingRequest>(args, 0)),
            nameof(IAmazonS3.PutObjectTaggingAsync) => PutObjectTagging(Require<PutObjectTaggingRequest>(args, 0)),
            nameof(IAmazonS3.GetObjectMetadataAsync) => GetObjectMetadata(),
            nameof(IDisposable.Dispose) => null,
            _ => throw new NotSupportedException($"Unexpected S3 call: {targetMethod.Name}")
        };
    }

    private static T Require<T>(object?[] arguments, int index) where T : class =>
        arguments.ElementAtOrDefault(index) as T ??
        throw new InvalidOperationException($"S3 call argument {index} was not a {typeof(T).Name}.");

    private Task<InitiateMultipartUploadResponse> Initiate(InitiateMultipartUploadRequest request)
    {
        InitiatedRequest = request;
        return Task.FromResult(new InitiateMultipartUploadResponse { UploadId = "upload-1" });
    }

    private async Task<UploadPartResponse> UploadPartAsync(UploadPartRequest request, CancellationToken cancellationToken)
    {
        if (request.PartNumber == FailPartNumber)
        {
            throw new AmazonS3Exception("provider secret")
            {
                StatusCode = HttpStatusCode.ServiceUnavailable
            };
        }
        var before = UploadedBytes.Length;
        await request.InputStream.CopyToAsync(UploadedBytes, cancellationToken);
        PartLengths.Add(checked((int)(UploadedBytes.Length - before)));
        return new UploadPartResponse
        {
            ETag = $"etag-{request.PartNumber}",
            PartNumber = request.PartNumber
        };
    }

    private Task<CompleteMultipartUploadResponse> Complete(CompleteMultipartUploadRequest request)
    {
        CompletedRequest = request;
        return Task.FromResult(new CompleteMultipartUploadResponse
        {
            ETag = "complete-etag",
            VersionId = "version-1"
        });
    }

    private Task<AbortMultipartUploadResponse> Abort()
    {
        AbortCalled = true;
        return Task.FromResult(new AbortMultipartUploadResponse());
    }

    private Task<string> Sign(GetPreSignedUrlRequest request)
    {
        PreSignedRequest = request;
        return Task.FromResult("https://s3.example.test/object?signature=secret");
    }

    private Task<GetObjectResponse> GetObject(GetObjectRequest request)
    {
        GetObjectRequest = request;
        return Task.FromResult(new GetObjectResponse { ResponseStream = new MemoryStream([1, 2, 3]) });
    }

    private Task<ListVersionsResponse> ListVersions(ListVersionsRequest request)
    {
        ListVersionsRequest = request;
        return Task.FromResult(ListVersionsResponse);
    }

    private Task<DeleteObjectResponse> DeleteObject(DeleteObjectRequest request)
    {
        DeleteObjectRequest = request;
        return Task.FromResult(new DeleteObjectResponse());
    }

    private Task<PutObjectResponse> PutObject(PutObjectRequest request)
    {
        PutObjectRequest = request;
        return Task.FromResult(new PutObjectResponse
        {
            ETag = "\"uploaded\"",
            VersionId = "uploaded-version"
        });
    }

    private Task<GetObjectTaggingResponse> GetObjectTagging(GetObjectTaggingRequest request)
    {
        GetObjectTaggingRequest = request;
        return Task.FromResult(GetObjectTaggingResponse);
    }

    private Task<PutObjectTaggingResponse> PutObjectTagging(PutObjectTaggingRequest request)
    {
        PutObjectTaggingRequest = request;
        return Task.FromResult(new PutObjectTaggingResponse());
    }

    private Task<GetObjectMetadataResponse> GetObjectMetadata() => Task.FromResult(MetadataResponse);
}
