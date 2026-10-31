using System.Diagnostics;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Configuration;
using CodeLogic.Core.Results;
using Xunit;

namespace Storage.Tests;

public sealed class StorageLibraryMutationTests
{
    [Fact]
    public async Task Stop_during_replacement_construction_reports_the_lifecycle_state()
    {
        using var directory = new TestDirectory();
        var initial = new FakeStorageBackend("Default", root: "/initial");
        var replacement = new FakeStorageBackend("Default", root: "/replacement");
        var createStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCreate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var createCount = 0;
        var factory = new FakeStorageBackendFactory((_, _) =>
        {
            if (Interlocked.Increment(ref createCount) == 1)
                return initial;

            createStarted.TrySetResult();
            releaseCreate.Task.GetAwaiter().GetResult();
            return replacement;
        });
        using var library = new global::CL.Storage.StorageLibrary([factory]);
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        await StorageLibraryTestSupport.InitializeAsync(
            library,
            context,
            configureLocal: local => local.Connections["Default"] = new() { RootPath = directory.Path });
        var add = Task.Run(() => library.AddOrUpdateConnectionAsync(
            "Default",
            new LocalConnectionConfig { RootPath = directory.Path },
            persist: false));

        try
        {
            await createStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(750));
            await library.OnStopAsync().WaitAsync(TimeSpan.FromMilliseconds(750));

            releaseCreate.TrySetResult();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => add);

            Assert.Contains("stopped", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, replacement.DisposeCount);
        }
        finally
        {
            releaseCreate.TrySetResult();
        }
    }

    [Fact]
    public async Task Cancelled_noncooperative_replacement_probe_defers_disposal_until_probe_settles()
    {
        using var directory = new TestDirectory();
        var initial = new FakeStorageBackend("Default", root: "/initial");
        var probeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProbe = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacement = new FakeStorageBackend(
            "Default",
            root: "/replacement",
            health: _ =>
            {
                probeStarted.TrySetResult();
                return releaseProbe.Task;
            },
            dispose: () =>
            {
                disposed.TrySetResult();
                return ValueTask.CompletedTask;
            });
        var backends = new Queue<IStorageBackend>([initial, replacement]);
        var factory = new FakeStorageBackendFactory((_, _) => backends.Dequeue());
        using var library = new global::CL.Storage.StorageLibrary([factory]);
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        await StorageLibraryTestSupport.InitializeAsync(
            library,
            context,
            configureLocal: local => local.Connections["Default"] = new() { RootPath = directory.Path });
        using var cancellation = new CancellationTokenSource();
        var add = library.AddOrUpdateConnectionAsync(
            "Default",
            new LocalConnectionConfig { RootPath = directory.Path },
            persist: false,
            cancellation.Token);
        await probeStarted.Task;

        cancellation.Cancel();
        try
        {
            var completed = await Task.WhenAny(add, Task.Delay(TimeSpan.FromMilliseconds(750)));
            Assert.Same(add, completed);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => add);

            Assert.Equal(0, replacement.DisposeCount);
            Assert.Equal("/initial", library.DefaultStorage.Root);

            releaseProbe.TrySetException(new InvalidOperationException("late probe failure"));
            await disposed.Task.WaitAsync(TimeSpan.FromMilliseconds(750));
            Assert.Equal(1, replacement.DisposeCount);
            Assert.Contains(((TestLogger)context.Logger).Errors,
                message => message.Contains("probe", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            releaseProbe.TrySetResult(Result.Success());
            try { await releaseProbe.Task; }
            catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public async Task Noncooperative_replacement_probe_honors_hard_timeout_and_is_not_published()
    {
        using var directory = new TestDirectory();
        var initial = new FakeStorageBackend("Default", root: "/initial");
        var releaseProbe = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacement = new FakeStorageBackend(
            "Default",
            root: "/replacement",
            health: _ => releaseProbe.Task,
            dispose: () =>
            {
                disposed.TrySetResult();
                return ValueTask.CompletedTask;
            });
        var backends = new Queue<IStorageBackend>([initial, replacement]);
        var factory = new FakeStorageBackendFactory((_, _) => backends.Dequeue());
        using var library = new global::CL.Storage.StorageLibrary([factory]);
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        await StorageLibraryTestSupport.InitializeAsync(
            library,
            context,
            storage => storage.HealthCheckTimeoutSeconds = 1,
            local => local.Connections["Default"] = new() { RootPath = directory.Path });
        var stopwatch = Stopwatch.StartNew();
        var add = library.AddOrUpdateConnectionAsync(
            "Default",
            new LocalConnectionConfig { RootPath = directory.Path },
            persist: false);

        try
        {
            var completed = await Task.WhenAny(add, Task.Delay(TimeSpan.FromMilliseconds(1800)));
            Assert.Same(add, completed);
            var result = await add;
            stopwatch.Stop();
            Assert.True(result.IsFailure);
            Assert.Equal(StorageErrors.TimeoutCode, result.Error!.Code);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(1800), $"Probe took {stopwatch.Elapsed}.");
            Assert.Equal(0, replacement.DisposeCount);
            Assert.Equal("/initial", library.DefaultStorage.Root);

            releaseProbe.TrySetResult(Result.Success());
            await disposed.Task.WaitAsync(TimeSpan.FromMilliseconds(750));
            Assert.Equal(1, replacement.DisposeCount);
        }
        finally
        {
            releaseProbe.TrySetResult(Result.Success());
            if (!add.IsCompleted)
                await add;
        }
    }

    [Fact]
    public async Task Local_add_and_remove_persist_to_storage_local_and_reload_from_real_CodeLogic_configuration()
    {
        using var directory = new TestDirectory();
        var libraryRoot = directory.CreateDirectory("library");
        var defaultRoot = directory.CreateDirectory("default");
        var archiveRoot = directory.CreateDirectory("archive");
        var context = StorageLibraryTestSupport.CreateContext(libraryRoot);
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(
            library,
            context,
            configureLocal: local => local.Connections["Default"] = new LocalConnectionConfig { RootPath = defaultRoot });

        var add = await library.AddOrUpdateConnectionAsync(
            "Archive",
            new LocalConnectionConfig { RootPath = archiveRoot });

        Assert.True(add.IsSuccess, add.Error?.Message);
        Assert.Equal(archiveRoot, library.GetStorage("archive").Root);
        Assert.True(File.Exists(Path.Combine(context.ConfigDirectory, "config.storage.local.json")));
        var reloadedAfterAdd = await ReloadLocalAsync(context.ConfigDirectory);
        Assert.Equal(archiveRoot, reloadedAfterAdd.Connections["Archive"].RootPath);

        var remove = await library.RemoveConnectionAsync("ARCHIVE");

        Assert.True(remove.IsSuccess, remove.Error?.Message);
        Assert.Throws<KeyNotFoundException>(() => library.GetStorage("Archive"));
        var reloadedAfterRemove = await ReloadLocalAsync(context.ConfigDirectory);
        Assert.DoesNotContain(reloadedAfterRemove.Connections.Keys, id =>
            string.Equals(id, "Archive", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Failed_local_replacement_preserves_the_old_backend_and_persisted_config()
    {
        using var directory = new TestDirectory();
        var libraryRoot = directory.CreateDirectory("library");
        var oldRoot = directory.CreateDirectory("old");
        var missingRoot = Path.Combine(directory.Path, "missing");
        var context = StorageLibraryTestSupport.CreateContext(libraryRoot);
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(
            library,
            context,
            configureLocal: local => local.Connections["Default"] = new LocalConnectionConfig { RootPath = oldRoot });
        await context.Configuration.SaveAsync(context.Configuration.Get<LocalStorageConfig>());
        var stableProxy = library.DefaultStorage;

        var replacement = await library.AddOrUpdateConnectionAsync(
            "default",
            new LocalConnectionConfig { RootPath = missingRoot });

        Assert.True(replacement.IsFailure);
        Assert.Equal(oldRoot, stableProxy.Root);
        Assert.Equal(oldRoot, context.Configuration.Get<LocalStorageConfig>().Connections["Default"].RootPath);
        Assert.Equal(oldRoot, (await ReloadLocalAsync(context.ConfigDirectory)).Connections["Default"].RootPath);
    }

    [Fact]
    public async Task Persistence_failure_does_not_publish_an_healthy_replacement()
    {
        using var directory = new TestDirectory();
        var libraryRoot = directory.CreateDirectory("library");
        var oldRoot = directory.CreateDirectory("old");
        var replacementRoot = directory.CreateDirectory("replacement");
        var context = StorageLibraryTestSupport.CreateContext(libraryRoot);
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(
            library,
            context,
            configureLocal: local => local.Connections["Default"] = new LocalConnectionConfig { RootPath = oldRoot });
        await context.Configuration.SaveAsync(context.Configuration.Get<LocalStorageConfig>());
        var configPath = Path.Combine(context.ConfigDirectory, "config.storage.local.json");
        var stableProxy = library.DefaultStorage;

        // CodeLogic writes config to a temp file and renames it over the target. Its temp name is unique per write
        // since CodeLogic 5.0.32 (it was "<file>.tmp" before), so the test cannot block the temp file. Instead a
        // directory takes the target's place: renaming a file over a directory fails on every OS, whatever the
        // temp name (a FileShare.None lock does not stop a rename on Linux, and permissions do not stop root).
        var persisted = await File.ReadAllBytesAsync(configPath);
        File.Delete(configPath);
        Directory.CreateDirectory(configPath);
        Result result;
        try
        {
            result = await library.AddOrUpdateConnectionAsync(
                "Default",
                new LocalConnectionConfig { RootPath = replacementRoot });
        }
        finally
        {
            Directory.Delete(configPath, recursive: true);
            await File.WriteAllBytesAsync(configPath, persisted);
        }

        Assert.True(result.IsFailure);
        Assert.Equal(oldRoot, stableProxy.Root);
        Assert.Equal(oldRoot, context.Configuration.Get<LocalStorageConfig>().Connections["Default"].RootPath);
        Assert.Equal(oldRoot, (await ReloadLocalAsync(context.ConfigDirectory)).Connections["Default"].RootPath);
    }

    [Fact]
    public async Task Runtime_only_local_mutations_stay_context_visible_but_never_leak_into_later_persistence()
    {
        using var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.CreateDirectory("library"));
        var defaultRoot = directory.CreateDirectory("default");
        var runtimeRoot = directory.CreateDirectory("runtime");
        var persistedRoot = directory.CreateDirectory("persisted");
        var laterRoot = directory.CreateDirectory("later");
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(
            library,
            context,
            configureLocal: local => local.Connections["Default"] = new() { RootPath = defaultRoot });
        await context.Configuration.SaveAsync(context.Configuration.Get<LocalStorageConfig>());
        var initialContextConfig = context.Configuration.Get<LocalStorageConfig>();

        Assert.True((await library.AddOrUpdateConnectionAsync(
            "RuntimeOnly", new LocalConnectionConfig { RootPath = runtimeRoot }, persist: false)).IsSuccess);

        Assert.Same(initialContextConfig, context.Configuration.Get<LocalStorageConfig>());
        Assert.Equal(runtimeRoot, initialContextConfig.Connections["RuntimeOnly"].RootPath);
        Assert.DoesNotContain((await ReloadLocalAsync(context.ConfigDirectory)).Connections.Keys, id => id == "RuntimeOnly");

        Assert.True((await library.AddOrUpdateConnectionAsync(
            "Persisted", new LocalConnectionConfig { RootPath = persistedRoot }, persist: true)).IsSuccess);

        var effectiveAfterSave = context.Configuration.Get<LocalStorageConfig>();
        Assert.Equal(runtimeRoot, effectiveAfterSave.Connections["RuntimeOnly"].RootPath);
        Assert.Equal(persistedRoot, effectiveAfterSave.Connections["Persisted"].RootPath);
        var diskAfterSave = await ReloadLocalAsync(context.ConfigDirectory);
        Assert.DoesNotContain(diskAfterSave.Connections.Keys, id => id == "RuntimeOnly");
        Assert.Equal(persistedRoot, diskAfterSave.Connections["Persisted"].RootPath);

        Assert.True((await library.RemoveConnectionAsync("Persisted", persist: false)).IsSuccess);
        Assert.DoesNotContain(context.Configuration.Get<LocalStorageConfig>().Connections.Keys, id => id == "Persisted");

        Assert.True((await library.AddOrUpdateConnectionAsync(
            "Later", new LocalConnectionConfig { RootPath = laterRoot }, persist: true)).IsSuccess);

        var finalEffective = context.Configuration.Get<LocalStorageConfig>();
        Assert.DoesNotContain(finalEffective.Connections.Keys, id => id == "Persisted");
        Assert.Equal(runtimeRoot, finalEffective.Connections["RuntimeOnly"].RootPath);
        var finalDisk = await ReloadLocalAsync(context.ConfigDirectory);
        Assert.Equal(persistedRoot, finalDisk.Connections["Persisted"].RootPath);
        Assert.Equal(laterRoot, finalDisk.Connections["Later"].RootPath);
        Assert.DoesNotContain(finalDisk.Connections.Keys, id => id == "RuntimeOnly");

        Assert.True((await library.RemoveConnectionAsync("RuntimeOnly", persist: false)).IsSuccess);
        Assert.True((await library.RemoveConnectionAsync("Persisted", persist: true)).IsSuccess);
        Assert.DoesNotContain(context.Configuration.Get<LocalStorageConfig>().Connections.Keys, id => id == "RuntimeOnly" || id == "Persisted");
        var diskAfterPersistedRemove = await ReloadLocalAsync(context.ConfigDirectory);
        Assert.DoesNotContain(diskAfterPersistedRemove.Connections.Keys, id => id == "Persisted" || id == "RuntimeOnly");
    }

    [Fact]
    public async Task Custom_backends_are_runtime_only_and_report_sanitized_information()
    {
        using var directory = new TestDirectory();
        var root = directory.CreateDirectory("default");
        var context = StorageLibraryTestSupport.CreateContext(directory.CreateDirectory("library"));
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(
            library,
            context,
            configureLocal: local => local.Connections["Default"] = new LocalConnectionConfig { RootPath = root });
        await context.Configuration.SaveAsync(context.Configuration.Get<LocalStorageConfig>());
        var backend = new FakeStorageBackend("Runtime", root: "/runtime");

        var result = library.RegisterBackend("Runtime", backend, ownsBackend: false);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("/runtime", library.GetStorage("runtime").Root);
        Assert.Contains(library.GetConnections(), connection => connection.Id == "Runtime" && connection.Enabled);
        Assert.DoesNotContain((await ReloadLocalAsync(context.ConfigDirectory)).Connections.Keys, id => id == "Runtime");
    }

    [Fact]
    public async Task Stop_during_backend_registration_reports_the_lifecycle_state()
    {
        using var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);
        var getterStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGetter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var getterCount = 0;
        var backend = new FakeStorageBackend(
            "Runtime",
            connectionIdGetter: () =>
            {
                if (Interlocked.Increment(ref getterCount) == 1)
                {
                    getterStarted.TrySetResult();
                    releaseGetter.Task.GetAwaiter().GetResult();
                }
                return "Runtime";
            });
        var registration = Task.Run(() => library.RegisterBackend("Runtime", backend));

        try
        {
            await getterStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(750));
            await library.OnStopAsync().WaitAsync(TimeSpan.FromMilliseconds(750));

            releaseGetter.TrySetResult();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => registration);

            Assert.Contains("stopped", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, backend.DisposeCount);
        }
        finally
        {
            releaseGetter.TrySetResult();
        }
    }

    [Fact]
    public async Task Failed_custom_replacement_health_check_preserves_the_old_backend()
    {
        using var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);
        var oldBackend = new FakeStorageBackend("Custom", root: "/old");
        var failedReplacement = new FakeStorageBackend(
            "Custom",
            root: "/failed",
            health: _ => Task.FromResult(Result.Failure(StorageErrors.Unavailable("offline"))));
        Assert.True(library.RegisterBackend("Custom", oldBackend).IsSuccess);
        var stableProxy = library.GetStorage("Custom");

        var result = library.RegisterBackend("CUSTOM", failedReplacement);

        Assert.True(result.IsFailure);
        Assert.Equal("/old", stableProxy.Root);
        Assert.Equal(0, oldBackend.DisposeCount);
        Assert.Equal(0, failedReplacement.DisposeCount);
    }

    [Fact]
    public async Task Runtime_local_add_rejects_case_insensitive_id_from_a_remote_config_section()
    {
        using var directory = new TestDirectory();
        var defaultRoot = directory.CreateDirectory("default");
        var localRoot = directory.CreateDirectory("local-shared");
        var context = StorageLibraryTestSupport.CreateContext(directory.CreateDirectory("library"));
        using var library = new global::CL.Storage.StorageLibrary();
        await library.OnConfigureAsync(context);
        await context.Configuration.LoadAllAsync();
        context.Configuration.Get<LocalStorageConfig>().Connections["Default"] = new() { RootPath = defaultRoot };
        context.Configuration.Get<S3StorageConfig>().Connections["Shared"] = new()
        {
            Enabled = false,
            Bucket = "bucket",
            Prefix = "prefix"
        };
        await library.OnInitializeAsync(context);

        var result = await library.AddOrUpdateConnectionAsync(
            "shared",
            new LocalConnectionConfig { RootPath = localRoot },
            persist: true);

        Assert.True(result.IsFailure);
        Assert.Equal(StorageErrors.ConflictCode, result.Error!.Code);
        Assert.Equal(StorageProvider.S3, Assert.Single(library.GetConnections(), connection =>
            string.Equals(connection.Id, "Shared", StringComparison.OrdinalIgnoreCase)).Provider);
        Assert.DoesNotContain(context.Configuration.Get<LocalStorageConfig>().Connections.Keys, id =>
            string.Equals(id, "shared", StringComparison.OrdinalIgnoreCase));
        Assert.Throws<KeyNotFoundException>(() => library.GetStorage("shared"));
    }

    [Fact]
    public async Task Owned_backends_are_disposed_once_and_unowned_backends_are_never_disposed()
    {
        using var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);
        var ownedFirst = new FakeStorageBackend("Owned");
        var ownedSecond = new FakeStorageBackend("Owned");
        var unowned = new FakeStorageBackend("Unowned");

        Assert.True(library.RegisterBackend("Owned", ownedFirst).IsSuccess);
        Assert.True(library.RegisterBackend("Owned", ownedSecond).IsSuccess);
        Assert.True(library.RegisterBackend("Unowned", unowned, ownsBackend: false).IsSuccess);
        Assert.True((await library.RemoveConnectionAsync("Unowned", persist: false)).IsSuccess);

        Assert.Equal(1, ownedFirst.DisposeCount);
        Assert.Equal(0, unowned.DisposeCount);
        await library.OnStopAsync();
        Assert.Equal(1, ownedSecond.DisposeCount);
        Assert.Equal(0, unowned.DisposeCount);
    }

    [Fact]
    public async Task Native_access_checks_types_and_session_lease_retains_backend_until_released()
    {
        using var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);
        var client = new NativeClient("native");
        var backend = new FakeStorageBackend("Session", nativeClient: client);
        Assert.True(library.RegisterBackend("Session", backend).IsSuccess);

        Assert.Same(client, library.GetNativeClient<NativeClient>("session"));
        Assert.Throws<InvalidOperationException>(() => library.GetNativeClient<MemoryStream>("session"));
        var opened = await library.OpenNativeConnectionAsync<NativeClient>("session");
        Assert.True(opened.IsSuccess, opened.Error?.Message);

        var removal = library.RemoveConnectionAsync("SESSION", persist: false);
        await Task.Delay(50);
        Assert.False(removal.IsCompleted);
        Assert.Equal(0, backend.DisposeCount);

        await opened.Value!.DisposeAsync();
        var removed = await removal;
        Assert.True(removed.IsSuccess, removed.Error?.Message);
        Assert.Equal(1, backend.SessionReleases);
        Assert.Equal(1, backend.DisposeCount);
    }

    private static async Task<LocalStorageConfig> ReloadLocalAsync(string configDirectory)
    {
        var manager = new ConfigurationManager(configDirectory);
        manager.Register<LocalStorageConfig>("storage.local");
        await manager.LoadAsync<LocalStorageConfig>();
        return manager.Get<LocalStorageConfig>();
    }

    private sealed record NativeClient(string Name);
}
