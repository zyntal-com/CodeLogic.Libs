using CL.Storage.Abstractions;
using CL.Storage.Models;
using CL.Storage.Sync;
using Xunit;

namespace Storage.Integration.Tests;

/// <summary>Sync against real servers: object-store times and three-way runs that settle.</summary>
public sealed class SyncLiveTests2
{
    [SftpAndS3Fact] // Skipped, not passed silently, when S3 is not configured
    public async Task Two_way_sync_with_an_object_store_settles_after_the_first_run()
    {
        await using var live = await LiveLibrary.StartAsync(("sftp", LiveServers.Sftp()), ("s3", CloudEmulators.S3()));
        var dir = $"settle-{Guid.NewGuid():N}";
        var sftp = live.Library.GetStorage("sftp");
        var s3 = live.Library.GetStorage("s3");
        for (var i = 0; i < 20; i++)
            await sftp.UploadBytesAsync($"{dir}/f{i:00}.txt", [(byte)i, 1, 2]);
        var options = new StorageSyncOptions
        {
            Direction = StorageSyncDirection.TwoWay,
            StateStore = new InMemoryStorageSyncStateStore(),
            SyncId = dir,
            Verify = true
        };
        try
        {
            var first = await live.Library.SyncAsync("sftp", dir, "s3", dir, options);
            var second = await live.Library.SyncAsync("sftp", dir, "s3", dir, options);
            var third = await live.Library.SyncAsync("sftp", dir, "s3", dir, options);

            Assert.True(first.IsSuccess, first.Error?.ToString());
            Assert.Equal(20, first.Value!.Copied);
            Assert.Empty(first.Value.Failed);
            Assert.Empty(second.Value!.Actions);
            Assert.Empty(third.Value!.Actions);
        }
        finally
        {
            await sftp.DeleteAsync(dir, new StorageDeleteOptions { Recursive = true, IgnoreMissing = true });
            await s3.DeleteAsync(dir, new StorageDeleteOptions { Recursive = true, IgnoreMissing = true });
        }
    }

    [SftpAndS3Fact] // Skipped, not passed silently, when S3 is not configured
    public async Task One_way_copies_to_an_object_store_keep_the_source_time()
    {
        await using var live = await LiveLibrary.StartAsync(("sftp", LiveServers.Sftp()), ("s3", CloudEmulators.S3()));
        var dir = $"mtime-{Guid.NewGuid():N}";
        var sftp = live.Library.GetStorage("sftp");
        var s3 = live.Library.GetStorage("s3");
        await sftp.UploadBytesAsync($"{dir}/f.txt", [1, 2, 3]);
        await sftp.SetTimestampsAsync($"{dir}/f.txt", new DateTimeOffset(2020, 5, 5, 5, 5, 5, TimeSpan.Zero));
        try
        {
            var synced = await live.Library.SyncAsync("sftp", dir, "s3", dir);
            var compared = await live.Library.CompareAsync("sftp", dir, "s3", dir);

            Assert.Equal(1, synced.Value!.Copied);
            var info = (await s3.GetInfoAsync($"{dir}/f.txt")).Value!;
            Assert.Equal(new DateTimeOffset(2020, 5, 5, 5, 5, 5, TimeSpan.Zero), DateTimeOffset.Parse(info.Metadata[StorageCompareOptions.ModifiedMetadataKey]));
            Assert.Equal(StorageDiffKind.Same, Assert.Single(compared.Value!.Entries).Kind);
        }
        finally
        {
            await sftp.DeleteAsync(dir, new StorageDeleteOptions { Recursive = true, IgnoreMissing = true });
            await s3.DeleteAsync(dir, new StorageDeleteOptions { Recursive = true, IgnoreMissing = true });
        }
    }
}
