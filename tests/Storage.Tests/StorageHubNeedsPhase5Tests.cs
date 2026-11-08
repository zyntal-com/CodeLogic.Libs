using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Models;
using CL.Storage.Providers;
using CL.Storage.Providers.Local;
using CL.Storage.Sync;
using Xunit;

namespace Storage.Tests;

/// <summary>Settings-keyed resources, tokens that outlive a registration, and incremental polling.</summary>
public sealed class ConnectionExtrasTests
{
    [Fact]
    public void Settings_keys_match_for_equal_settings_and_hide_secrets()
    {
        var a = new SftpConnectionConfig { Host = "h", Username = "u", Password = "hunter2", HostKeyFingerprints = ["SHA256:x"] };
        var b = new SftpConnectionConfig { Host = "h", Username = "u", Password = "hunter2", HostKeyFingerprints = ["SHA256:x"] };
        var c = new SftpConnectionConfig { Host = "h", Username = "u", Password = "other", HostKeyFingerprints = ["SHA256:x"] };

        Assert.Equal(ProviderSettingsKey.For(a), ProviderSettingsKey.For(b));
        Assert.NotEqual(ProviderSettingsKey.For(a), ProviderSettingsKey.For(c));
        Assert.DoesNotContain("hunter2", ProviderSettingsKey.For(a));
    }

    [Fact]
    public async Task A_shared_resource_lingers_and_is_reused_by_the_next_user()
    {
        var key = Guid.NewGuid().ToString("N");
        var created = 0;
        var disposed = 0;
        object Create() { Interlocked.Increment(ref created); return new object(); }
        ValueTask Dispose(object _) { Interlocked.Increment(ref disposed); return ValueTask.CompletedTask; }

        var first = SharedResources.Acquire(key, Create, Dispose);
        var lingering = SharedResources.ReleaseAsync(key, TimeSpan.FromMilliseconds(300));
        var second = SharedResources.Acquire(key, Create, Dispose);
        await lingering;

        Assert.Same(first, second);
        Assert.Equal((1, 0), (created, disposed));
        await SharedResources.ReleaseAsync(key, TimeSpan.Zero);
        Assert.Equal(1, disposed);
    }

    [Fact]
    public async Task Continuation_tokens_keep_working_after_re_registering_the_same_settings()
    {
        using var directory = new TestDirectory();
        var root = directory.CreateDirectory("files");
        for (var i = 0; i < 5; i++) await File.WriteAllTextAsync(Path.Combine(root, $"f{i}.txt"), "x");
        var factory = new LocalStorageBackendFactory();
        var config = new LocalConnectionConfig { RootPath = root };

        await using var before = (LocalStorageBackend)factory.Create("old-id", config, 1024);
        var first = (await before.ListAsync("", new StorageListOptions { PageSize = 2 })).Value!;
        // The caller retires the registration and registers the same settings under a new id.
        await using var after = (LocalStorageBackend)factory.Create("new-id", config, 1024);
        var rest = (await after.ListAsync("", new StorageListOptions { PageSize = 10, ContinuationToken = first.ContinuationToken })).Value!;

        Assert.Equal(["f0.txt", "f1.txt"], first.Items.Select(item => item.Path));
        Assert.Equal(["f2.txt", "f3.txt", "f4.txt"], rest.Items.Select(item => item.Path));
        Assert.Equal(before.ListingScope, after.ListingScope);
    }

    [Fact]
    public async Task Incremental_polls_list_only_changed_folders_and_catch_the_rest_on_a_full_rescan()
    {
        using var directory = new TestDirectory();
        var storage = new LocalStorageBackend("local", new LocalConnectionConfig { RootPath = directory.Path });
        await storage.UploadBytesAsync("sub/deep/old.txt", [1]);
        var baseline = new Dictionary<string, StorageItem>(StringComparer.Ordinal);
        await foreach (var item in storage.EnumerateItemsAsync("", new StorageListOptions { Recursive = true }))
            baseline[item.Value!.Path] = item.Value;

        await storage.UploadBytesAsync("top.txt", [1]);                  // root changes: found at once
        await storage.UploadBytesAsync("sub/deep/new.txt", [1]);         // only "sub/deep" changes: waits
        await storage.UploadBytesAsync("other/new.txt", [1]);            // "other" is new: listed at once
        // Folder times are set explicitly instead of relying on the file system's time
        // granularity: "sub" keeps the time the baseline saw, "sub/deep" moves on.
        Directory.SetLastWriteTimeUtc(Path.Combine(directory.Path, "sub"), baseline["sub"].LastModified!.Value.UtcDateTime);
        Directory.SetLastWriteTimeUtc(Path.Combine(directory.Path, "sub", "deep"), baseline["sub/deep"].LastModified!.Value.UtcDateTime.AddMinutes(5));

        var incremental = (await StorageWatch.IncrementalSnapshotAsync(storage, "", baseline, CancellationToken.None))!;

        Assert.Contains("top.txt", incremental.Keys);
        Assert.Contains("other/new.txt", incremental.Keys);
        Assert.DoesNotContain("sub/deep/new.txt", incremental.Keys);
        Assert.Contains("sub/deep/old.txt", incremental.Keys);
    }
}
