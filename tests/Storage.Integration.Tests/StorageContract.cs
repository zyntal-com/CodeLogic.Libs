using System.Text;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using Xunit;

namespace Storage.Integration.Tests;

/// <summary>Provider-neutral behaviour every live backend must satisfy.</summary>
internal static class StorageContract
{
    public static async Task RoundTripAsync(IStorageBackend storage)
    {
        var dir = $"cl-it-{Guid.NewGuid():N}";
        var content = Encoding.UTF8.GetBytes("hello live storage");
        try
        {
            var upload = await storage.UploadBytesAsync($"{dir}/nested/a.txt", content);
            Assert.True(upload.IsSuccess, upload.Error?.ToString());

            var info = await storage.GetInfoAsync($"{dir}/nested/a.txt");
            Assert.True(info.IsSuccess, info.Error?.ToString());
            Assert.Equal(StorageItemType.File, info.Value!.ItemType);
            Assert.Equal(content.Length, info.Value.Size);
            Assert.True((await storage.ExistsAsync($"{dir}/nested/a.txt")).Value);

            var listing = await storage.ListAsync($"{dir}/nested");
            Assert.True(listing.IsSuccess, listing.Error?.ToString());
            Assert.Contains(listing.Value!.Items, item => item.Name == "a.txt");

            var download = await storage.DownloadBytesAsync($"{dir}/nested/a.txt");
            Assert.Equal(content, download.Value);

            var range = await storage.DownloadBytesAsync($"{dir}/nested/a.txt", new StorageDownloadOptions { Offset = 6, Length = 4 });
            Assert.Equal("live", Encoding.UTF8.GetString(range.Value!));

            var copy = await storage.CopyAsync($"{dir}/nested/a.txt", $"{dir}/b.txt");
            Assert.True(copy.IsSuccess, copy.Error?.ToString());
            var move = await storage.MoveAsync($"{dir}/b.txt", $"{dir}/c.txt");
            Assert.True(move.IsSuccess, move.Error?.ToString());
            Assert.False((await storage.ExistsAsync($"{dir}/b.txt")).Value);

            var conflict = await storage.UploadBytesAsync($"{dir}/c.txt", content, new StorageUploadOptions { Overwrite = false });
            Assert.Equal(StorageErrors.ConflictCode, conflict.Error?.Code);

            var replaced = await storage.UploadBytesAsync($"{dir}/c.txt", Encoding.UTF8.GetBytes("v2"));
            Assert.True(replaced.IsSuccess, replaced.Error?.ToString());
            Assert.Equal("v2", Encoding.UTF8.GetString((await storage.DownloadBytesAsync($"{dir}/c.txt")).Value!));
        }
        finally
        {
            var cleanup = await storage.DeleteAsync(dir, new StorageDeleteOptions { Recursive = true, IgnoreMissing = true });
            Assert.True(cleanup.IsSuccess, cleanup.Error?.ToString());
        }
        Assert.False((await storage.ExistsAsync(dir)).Value);
    }

    /// <summary>Renames a folder tree natively and checks every file arrived and the source is gone.</summary>
    public static async Task DirectoryMoveAsync(IStorageBackend storage, bool atomic = true)
    {
        Assert.True(storage.Capabilities.Supports(StorageFeature.DirectoryMove));
        // WebDAV may move a collection member by member (207 Multi-Status), so it is not atomic.
        Assert.Equal(atomic, storage.Capabilities.Supports(StorageFeature.AtomicMove));
        var dir = $"cl-move-{Guid.NewGuid():N}";
        try
        {
            foreach (var file in new[] { "a.txt", "sub/b.txt", "sub/deeper/c.txt" })
                Assert.True((await storage.UploadBytesAsync($"{dir}/src/{file}", Encoding.UTF8.GetBytes(file))).IsSuccess);

            var moved = await storage.MoveAsync($"{dir}/src", $"{dir}/renamed");
            Assert.True(moved.IsSuccess, moved.Error?.ToString());

            Assert.False((await storage.ExistsAsync($"{dir}/src")).Value);
            foreach (var file in new[] { "a.txt", "sub/b.txt", "sub/deeper/c.txt" })
                Assert.Equal(file, Encoding.UTF8.GetString((await storage.DownloadBytesAsync($"{dir}/renamed/{file}")).Value!));

            Assert.True((await storage.UploadBytesAsync($"{dir}/other/x.txt", [1])).IsSuccess);
            var blocked = await storage.MoveAsync($"{dir}/renamed", $"{dir}/other", new StorageTransferOptions { Overwrite = false });
            Assert.Equal(StorageErrors.ConflictCode, blocked.Error?.Code);

            // An existing directory is never replaced (that would delete what it holds), overwrite or not.
            var replaced = await storage.MoveAsync($"{dir}/renamed", $"{dir}/other", new StorageTransferOptions { Overwrite = true });
            Assert.Equal(StorageErrors.ConflictCode, replaced.Error?.Code);
            Assert.True((await storage.ExistsAsync($"{dir}/other/x.txt")).Value);
            Assert.True((await storage.ExistsAsync($"{dir}/renamed/sub/deeper/c.txt")).Value);
        }
        finally
        {
            await storage.DeleteAsync(dir, new StorageDeleteOptions { Recursive = true, IgnoreMissing = true });
        }
    }

    /// <summary>Skip, rename, and size-based overwrite behave the same on every provider.</summary>
    public static async Task ConflictPoliciesAsync(IStorageBackend storage)
    {
        var dir = $"cl-conflict-{Guid.NewGuid():N}";
        try
        {
            Assert.True((await storage.UploadBytesAsync($"{dir}/f.txt", Encoding.UTF8.GetBytes("v1"))).IsSuccess);

            var skipped = await storage.UploadBytesAsync($"{dir}/f.txt", Encoding.UTF8.GetBytes("v2"), new StorageUploadOptions { ConflictPolicy = StorageConflictPolicy.Skip });
            Assert.True(skipped.IsSuccess, skipped.Error?.ToString());
            Assert.Equal("v1", Encoding.UTF8.GetString((await storage.DownloadBytesAsync($"{dir}/f.txt")).Value!));

            var renamed = await storage.UploadBytesAsync($"{dir}/f.txt", Encoding.UTF8.GetBytes("v3"), new StorageUploadOptions { ConflictPolicy = StorageConflictPolicy.Rename });
            Assert.True(renamed.IsSuccess, renamed.Error?.ToString());
            Assert.Equal("v3", Encoding.UTF8.GetString((await storage.DownloadBytesAsync($"{dir}/f (1).txt")).Value!));

            var sameSize = await storage.UploadBytesAsync($"{dir}/f.txt", Encoding.UTF8.GetBytes("v4"), new StorageUploadOptions { ConflictPolicy = StorageConflictPolicy.OverwriteIfSizeDiffers });
            Assert.True(sameSize.IsSuccess);
            Assert.Equal("v1", Encoding.UTF8.GetString((await storage.DownloadBytesAsync($"{dir}/f.txt")).Value!));

            var longer = await storage.UploadBytesAsync($"{dir}/f.txt", Encoding.UTF8.GetBytes("v5-longer"), new StorageUploadOptions { ConflictPolicy = StorageConflictPolicy.OverwriteIfSizeDiffers });
            Assert.True(longer.IsSuccess);
            Assert.Equal("v5-longer", Encoding.UTF8.GetString((await storage.DownloadBytesAsync($"{dir}/f.txt")).Value!));
        }
        finally
        {
            await storage.DeleteAsync(dir, new StorageDeleteOptions { Recursive = true, IgnoreMissing = true });
        }
    }

    /// <summary>Append and resume continue a partial file on providers that support appending.</summary>
    public static async Task AppendAndResumeAsync(IStorageBackend storage)
    {
        var dir = $"cl-resume-{Guid.NewGuid():N}";
        var full = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 5000).Select(i => (char)('a' + i % 26))));
        try
        {
            Assert.True((await storage.UploadBytesAsync($"{dir}/big.bin", full[..1234])).IsSuccess);
            var resumed = await storage.UploadAsync($"{dir}/big.bin", new MemoryStream(full), new StorageUploadOptions { ConflictPolicy = StorageConflictPolicy.Resume, SourceIdentity = "contract-big", SourceIdentityIsContentVersion = true });
            Assert.True(resumed.IsSuccess, resumed.Error?.ToString());
            Assert.Equal(full, (await storage.DownloadBytesAsync($"{dir}/big.bin")).Value);

            Assert.True((await storage.AppendAsync($"{dir}/log.txt", new MemoryStream(Encoding.UTF8.GetBytes("one|")))).IsSuccess);
            Assert.True((await storage.AppendAsync($"{dir}/log.txt", new MemoryStream(Encoding.UTF8.GetBytes("two|")))).IsSuccess);
            Assert.Equal("one|two|", Encoding.UTF8.GetString((await storage.DownloadBytesAsync($"{dir}/log.txt")).Value!));
        }
        finally
        {
            await storage.DeleteAsync(dir, new StorageDeleteOptions { Recursive = true, IgnoreMissing = true });
        }
    }

    public static async Task MissingItemIsNotFoundAsync(IStorageBackend storage)
    {
        var info = await storage.GetInfoAsync($"missing-{Guid.NewGuid():N}.txt");
        Assert.Equal(StorageErrors.NotFoundCode, info.Error?.Code);
        var download = await storage.DownloadBytesAsync($"missing-{Guid.NewGuid():N}.txt");
        Assert.Equal(StorageErrors.NotFoundCode, download.Error?.Code);
    }

    public static async Task WrongCredentialsAreAuthenticationFailuresAsync(IStorageBackend storage)
    {
        var health = await storage.CheckHealthAsync();
        Assert.Equal(StorageErrors.AuthenticationFailedCode, health.Error?.Code);
        Assert.False(StorageErrorInfo.IsTransient(health.Error));
    }

    public static async Task ClosedPortIsConnectionFailureAsync(IStorageBackend storage)
    {
        var health = await storage.CheckHealthAsync();
        Assert.NotNull(health.Error);
        Assert.Contains(health.Error!.Code, new[] { StorageErrors.ConnectionFailedCode, StorageErrors.TimeoutCode });
        Assert.True(StorageErrorInfo.IsTransient(health.Error));
    }
}
