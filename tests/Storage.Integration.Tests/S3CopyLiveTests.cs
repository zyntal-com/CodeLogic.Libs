using Amazon.S3;
using Amazon.S3.Model;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Providers.S3;
using Xunit;

namespace Storage.Integration.Tests;

/// <summary>S3 copies too large for one request, and create-only copies the server itself enforces.</summary>
public sealed class S3CopyLiveTests
{
    private const int MiB = 1024 * 1024;

    private static async Task<(AmazonS3Client Client, Func<Func<Task>?, S3StorageBackend> Create)> ConnectAsync()
    {
        var config = CloudEmulators.S3();
        var client = new AmazonS3Client(config.AccessKey, config.SecretKey,
            new AmazonS3Config { ServiceURL = config.ServiceUrl, ForcePathStyle = true, AuthenticationRegion = config.Region });
        try { await client.PutBucketAsync(new PutBucketRequest { BucketName = config.Bucket }); }
        catch (AmazonS3Exception error) when (error.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists") { }
        var prefix = $"s3copy-{Guid.NewGuid():N}";
        // A 6 MiB threshold stands in for S3's 5 GiB single-copy limit.
        return (client, hook => new S3StorageBackend("s3copy", client, config.Bucket, prefix,
            multipartPartSizeBytes: 5 * MiB, multipartThresholdBytes: 5 * MiB)
        {
            MultipartCopyThresholdBytes = 6 * MiB,
            BeforeConditionalCopy = hook
        });
    }

    [S3Fact]
    public async Task An_object_over_the_single_copy_limit_is_copied_part_by_part_with_its_metadata()
    {
        var (client, create) = await ConnectAsync();
        using var _ = client;
        await using var storage = create(null);
        var content = Enumerable.Range(0, 13 * MiB).Select(i => (byte)(i % 253)).ToArray();
        Assert.True((await storage.UploadBytesAsync("big.bin", content, new StorageUploadOptions
        {
            ContentType = "application/x-test",
            Metadata = new Dictionary<string, string> { ["origin"] = "live-test" }
        })).IsSuccess);

        var copied = await storage.CopyAsync("big.bin", "copy.bin", new StorageTransferOptions { Overwrite = false });

        Assert.True(copied.IsSuccess, copied.Error?.ToString());
        var info = (await storage.GetInfoAsync("copy.bin")).Value!;
        Assert.Equal(content.Length, info.Size);
        Assert.Equal("application/x-test", info.ContentType);
        Assert.Equal("live-test", info.Metadata["origin"]);
        Assert.Equal(content, (await storage.DownloadBytesAsync("copy.bin")).Value!);
    }

    [S3Fact]
    public async Task A_create_only_copy_is_refused_by_the_server_when_the_destination_appears_after_the_check()
    {
        var (client, create) = await ConnectAsync();
        using var _ = client;
        IStorageBackendHolder holder = new();
        await using var storage = create(async () => await holder.Storage!.UploadBytesAsync("target.bin", [7, 7, 7]));
        holder.Storage = storage;
        Assert.True((await storage.UploadBytesAsync("small.bin", [1])).IsSuccess);
        Assert.True((await storage.UploadBytesAsync("large.bin", new byte[7 * MiB])).IsSuccess);

        Assert.True((await storage.UploadBytesAsync("empty.bin", [])).IsSuccess);
        // Copies below the single-request limit are one CopyObject, whose If-None-Match some
        // servers (MinIO) ignore; the connection reports that honestly, and only there the race is not refused.
        var singleRequest = await ((CL.Storage.Abstractions.IStorageConditionEnforcementSource)storage)
            .GetEnforcementAsync(CL.Storage.Abstractions.StorageConditionKind.CreateOnly, serverSideCopy: true, default);

        foreach (var source in new[] { "small.bin", "large.bin", "empty.bin" })
        {
            var copied = await storage.CopyAsync(source, "target.bin", new StorageTransferOptions { Overwrite = false });
            if (source == "large.bin" || singleRequest == StorageConditionEnforcement.Atomic)
            {
                Assert.True(copied.Error?.Code == StorageErrors.ConflictCode, $"{source}: {copied.Error}");
                Assert.Equal([7, 7, 7], (await storage.DownloadBytesAsync("target.bin")).Value!);
            }
            else
            {
                Assert.Equal(StorageConditionEnforcement.CheckedBeforeCommit, singleRequest);
            }
            Assert.True((await storage.DeleteAsync("target.bin")).IsSuccess);
        }
    }

    [S3Fact]
    public async Task Create_only_copies_succeed_when_the_destination_is_free()
    {
        var (client, create) = await ConnectAsync();
        using var _ = client;
        await using var storage = create(null);
        Assert.True((await storage.UploadBytesAsync("small.bin", [1, 2, 3], new StorageUploadOptions { ContentType = "text/x-small" })).IsSuccess);
        Assert.True((await storage.UploadBytesAsync("empty.bin", [])).IsSuccess);

        Assert.True((await storage.CopyAsync("small.bin", "a.bin", new StorageTransferOptions { Overwrite = false })).IsSuccess);
        Assert.True((await storage.CopyAsync("empty.bin", "b.bin", new StorageTransferOptions { Overwrite = false })).IsSuccess);

        Assert.Equal([1, 2, 3], (await storage.DownloadBytesAsync("a.bin")).Value!);
        Assert.Equal("text/x-small", (await storage.GetInfoAsync("a.bin")).Value!.ContentType);
        Assert.Equal(0, (await storage.GetInfoAsync("b.bin")).Value!.Size);
    }

    private sealed class IStorageBackendHolder
    {
        public S3StorageBackend? Storage { get; set; }
    }
}
