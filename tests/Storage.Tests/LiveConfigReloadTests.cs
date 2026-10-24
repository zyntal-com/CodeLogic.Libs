using System.Text;
using System.Text.Json.Nodes;
using CL.Storage.Configuration;
using CodeLogic.Core.Events;
using CodeLogic.Framework.Libraries;
using Xunit;
using StorageLib = CL.Storage.StorageLibrary;

namespace Storage.Tests;

/// <summary>
/// Live config changes (IConfigReloadable) through a real <see cref="LibraryManager"/>, so the
/// change notifications run exactly as in an application. Local connections only.
/// </summary>
public sealed class LiveConfigReloadTests : IAsyncLifetime
{
    private readonly TestDirectory _dir = new();
    private LibraryManager _manager = null!;
    private StorageLib _storage = null!;
    private string _localConfigPath = null!;

    public async Task InitializeAsync()
    {
        var root = _dir.CreateDirectory("codelogic");
        var libDir = Path.Combine(root, "Libraries", "CL.Storage");
        Directory.CreateDirectory(libDir);
        _localConfigPath = Path.Combine(libDir, "config.storage.local.json");
        await File.WriteAllTextAsync(_localConfigPath, new JsonObject
        {
            ["connections"] = new JsonObject
            {
                ["Default"] = new JsonObject { ["rootPath"] = _dir.CreateDirectory("default") },
                ["Other"] = new JsonObject { ["rootPath"] = _dir.CreateDirectory("other") }
            }
        }.ToJsonString());

        _manager = new LibraryManager(new EventBus()) { FrameworkRootPath = root };
        await _manager.LoadLibraryAsync<StorageLib>();
        await _manager.ConfigureAllAsync();
        await _manager.InitializeAllAsync();
        await _manager.StartAllAsync();
        _storage = (StorageLib)_manager.GetLibrary("CL.Storage")!;
    }

    public async Task DisposeAsync()
    {
        await _manager.StopAllAsync();
        _manager.Dispose();
        _dir.Dispose();
    }

    private object BackendOf(string id)
    {
        using var lease = _storage.AcquireOperation(id);
        return lease.Backend;
    }

    private JsonNode LocalConfig() => JsonNode.Parse(File.ReadAllText(_localConfigPath))!;

    private Task<CodeLogic.Core.Configuration.ConfigApplyResult> UpdateLocalAsync(JsonNode config) =>
        _manager.UpdateConfigAsync("CL.Storage", "storage.local", config.ToJsonString(), changedBy: "test");

    private async Task WriteAsync(string connectionId, string path, string text)
    {
        using var content = new MemoryStream(Encoding.UTF8.GetBytes(text));
        var result = await _storage.GetStorage(connectionId).UploadAsync(path, content);
        Assert.True(result.IsSuccess, result.Error?.Message);
    }

    [Fact]
    public async Task Changing_one_connection_swaps_only_that_backend()
    {
        var defaultBackend = BackendOf("Default");
        var otherBackend = BackendOf("Other");
        var moved = _dir.CreateDirectory("other-moved");

        var config = LocalConfig();
        config["connections"]!["Other"]!["rootPath"] = moved;
        var result = await UpdateLocalAsync(config);

        Assert.True(result.Applied, result.ToString());
        Assert.Same(defaultBackend, BackendOf("Default"));
        Assert.NotSame(otherBackend, BackendOf("Other"));

        await WriteAsync("Other", "after.txt", "moved");
        Assert.True(File.Exists(Path.Combine(moved, "after.txt")));
    }

    [Fact]
    public async Task Added_and_removed_connections_are_registered_and_retired()
    {
        var defaultBackend = BackendOf("Default");
        var config = LocalConfig();
        var connections = config["connections"]!.AsObject();
        connections.Remove("Other");
        connections["Added"] = new JsonObject { ["rootPath"] = _dir.CreateDirectory("added") };

        var result = await UpdateLocalAsync(config);

        Assert.True(result.Applied, result.ToString());
        var ids = _storage.GetConnections().Select(c => c.Id).ToList();
        Assert.Contains("Added", ids);
        Assert.DoesNotContain("Other", ids);
        Assert.False(_storage.TryGetStorage("Other", out _));
        Assert.Same(defaultBackend, BackendOf("Default"));
    }

    [Fact]
    public async Task Unhealthy_replacement_keeps_the_current_backend_and_reports()
    {
        var otherBackend = BackendOf("Other");
        var notADirectory = Path.Combine(_dir.Path, "plain-file.txt");
        await File.WriteAllTextAsync(notADirectory, "x");

        var config = LocalConfig();
        config["connections"]!["Other"]!["rootPath"] = notADirectory;
        var result = await UpdateLocalAsync(config);

        Assert.True(result.IsValid);
        Assert.False(result.Applied);
        Assert.Same(otherBackend, BackendOf("Other"));
        Assert.True((await _manager.GetHealthAsync())["CL.Storage"].IsDegraded);
    }

    [Fact]
    public async Task Removing_the_default_connection_is_refused()
    {
        var defaultBackend = BackendOf("Default");
        var config = LocalConfig();
        config["connections"]!.AsObject().Remove("Default");

        var result = await UpdateLocalAsync(config);

        Assert.False(result.Applied);
        Assert.Same(defaultBackend, BackendOf("Default"));
    }

    [Fact]
    public async Task Own_persisted_changes_do_not_deadlock_or_apply_twice()
    {
        var root = _dir.CreateDirectory("third");
        var add = _storage.AddOrUpdateConnectionAsync("Third", new LocalConnectionConfig { RootPath = root }, persist: true);

        Assert.Same(add, await Task.WhenAny(add, Task.Delay(TimeSpan.FromSeconds(10))));
        Assert.True((await add).IsSuccess);
        Assert.Contains("Third", LocalConfig()["connections"]!.AsObject().Select(p => p.Key));

        var remove = _storage.RemoveConnectionAsync("Third", persist: true);
        Assert.Same(remove, await Task.WhenAny(remove, Task.Delay(TimeSpan.FromSeconds(10))));
        Assert.True((await remove).IsSuccess);
        Assert.False(_storage.TryGetStorage("Third", out _));
    }

    [Fact]
    public async Task Persisting_one_connection_keeps_the_others_transfer_limits()
    {
        var config = LocalConfig();
        config["connections"]!["Other"]!["transferLimits"] = new JsonObject { ["maxUploadBytesPerSecond"] = 2048 };
        Assert.True((await UpdateLocalAsync(config)).Applied);

        var add = await _storage.AddOrUpdateConnectionAsync(
            "Third", new LocalConnectionConfig { RootPath = _dir.CreateDirectory("third-limits") }, persist: true);

        Assert.True(add.IsSuccess);
        Assert.Equal(2048, LocalConfig()["connections"]!["Other"]!["transferLimits"]!["maxUploadBytesPerSecond"]!.GetValue<long>());
    }

    [Fact]
    public async Task Library_limits_change_live()
    {
        var result = await _manager.UpdateConfigAsync(
            "CL.Storage",
            "storage",
            """{"enabled":true,"defaultConnection":"Default","maxTotalUploadBytesPerSecond":1048576}""");

        Assert.True(result.Applied, result.ToString());
        Assert.NotNull(CL.Storage.Registry.StorageTransferPipeline.LimitsFor(BackendOf("Other")).TotalUpload);
    }
}
