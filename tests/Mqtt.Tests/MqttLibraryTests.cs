using System.Text.Json.Nodes;
using CL.Mqtt;
using CL.Mqtt.Models;
using CodeLogic.Core.Configuration;
using CodeLogic.Core.Events;
using CodeLogic.Framework.Libraries;
using Xunit;

namespace Mqtt.Tests;

/// <summary>
/// The library through a real <see cref="LibraryManager"/>: config file → embedded broker → a configured client
/// connecting to it, health, and live config changes.
/// </summary>
public sealed class MqttLibraryTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cl_mqtt_lib_" + Guid.NewGuid().ToString("N"));
    private readonly int _port = TestBroker.FreePort();
    private LibraryManager _manager = null!;
    private MqttLibrary _mqtt = null!;
    private string _configPath = null!;

    public async Task InitializeAsync()
    {
        var libDir = Path.Combine(_root, "Libraries", "CL.Mqtt");
        Directory.CreateDirectory(libDir);
        _configPath = Path.Combine(libDir, "config.mqtt.json");
        var config = new JsonObject
        {
            ["enabled"] = true,
            ["defaultConnection"] = "Default",
            ["connections"] = new JsonObject { ["Default"] = Connection() },
            ["broker"] = new JsonObject
            {
                ["enabled"] = true,
                ["port"] = _port,
                ["bindAddress"] = "127.0.0.1",
                ["allowAnonymous"] = true
            }
        };
        await File.WriteAllTextAsync(_configPath, config.ToJsonString());

        _manager = new LibraryManager(new EventBus()) { FrameworkRootPath = _root };
        await _manager.LoadLibraryAsync<MqttLibrary>();
        await _manager.ConfigureAllAsync();
        await _manager.InitializeAllAsync();
        await _manager.StartAllAsync();
        _mqtt = _manager.GetLibrary<MqttLibrary>()!;
    }

    public async Task DisposeAsync()
    {
        await _manager.StopAllAsync();
        _manager.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* logger may still hold its file */ }
    }

    [Fact]
    public async Task Sync_dispose_does_not_wait_for_a_blocked_retained_writer_and_async_dispose_joins_it()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new CL.Mqtt.Services.RetainedMessageStore(Path.Combine(_root, "blocked-retained.json"),
            CodeLogic.Core.Logging.NullLogger.Instance, async () => { entered.TrySetResult(); await release.Task; });
        typeof(CL.Mqtt.Services.MqttBrokerService).GetField("_retained", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(_mqtt.Broker, store);
        store.Schedule([new MQTTnet.MqttApplicationMessageBuilder().WithTopic("test").WithPayload("test").Build()]);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Run(_mqtt.Dispose).WaitAsync(TimeSpan.FromSeconds(2));
            var cleanup = _mqtt.DisposeAsync().AsTask();
            Assert.False(cleanup.IsCompleted);
            var stopping = _mqtt.OnStopAsync();
            Assert.False(stopping.IsCompleted);
            release.TrySetResult();
            await Task.WhenAll(cleanup, stopping).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { release.TrySetResult(); await _mqtt.DisposeAsync(); }
    }

    private JsonObject Connection(string? clientId = null) => new()
    {
        ["host"] = "127.0.0.1",
        ["port"] = _port,
        ["clientId"] = clientId ?? $"lib-{Guid.NewGuid():N}",
        ["timeoutSeconds"] = 5,
        ["reconnectMinDelaySeconds"] = 1,
        ["reconnectMaxDelaySeconds"] = 2
    };

    private JsonNode Config() => JsonNode.Parse(File.ReadAllText(_configPath))!;

    private Task<ConfigApplyResult> UpdateAsync(JsonNode config) =>
        _manager.UpdateConfigAsync("CL.Mqtt", "mqtt", config.ToJsonString(), changedBy: "test");

    [Fact]
    public async Task Start_runs_the_broker_and_connects_the_default_client()
    {
        Assert.True(_mqtt.HasBroker);
        Assert.True(_mqtt.Broker.IsRunning);
        var client = _mqtt.Client().ValueOrThrow();
        Assert.True(client.IsConnected);
        Assert.Same(client, _mqtt.Client("default").ValueOrThrow());
        Assert.Equal(MqttErrorCodes.ConnectionNotFound, _mqtt.Client("missing").Error!.Code);

        var health = await _mqtt.HealthCheckAsync();
        Assert.True(health.IsHealthy, health.Message);

        var inbox = new Inbox();
        (await client.SubscribeAsync("lib/#", inbox.Handle)).ValueOrThrow();
        (await _mqtt.Broker.PublishAsync("lib/hello", "world")).ThrowIfFailure();
        Assert.Equal("world", (await inbox.WaitForAsync(1))[0].PayloadAsString());
    }

    [Fact]
    public async Task Changing_a_connection_reconfigures_it_in_place_and_keeps_subscriptions()
    {
        var client = _mqtt.Client().ValueOrThrow();
        var inbox = new Inbox();
        (await client.SubscribeAsync("kept/#", inbox.Handle)).ValueOrThrow();

        var config = Config();
        config["connections"]!["Default"] = Connection(clientId: "renamed-client");
        var result = await UpdateAsync(config);

        Assert.True(result.Applied, result.ToString());
        Assert.Same(client, _mqtt.Client().ValueOrThrow());
        Assert.Equal("renamed-client", client.ClientId);
        Assert.True(client.IsConnected);
        (await _mqtt.Broker.PublishAsync("kept/after", "yes")).ThrowIfFailure();
        await inbox.WaitForAsync(1);
    }

    [Fact]
    public async Task Connections_can_be_added_and_removed_live()
    {
        var config = Config();
        config["connections"]!["Second"] = Connection();
        Assert.True((await UpdateAsync(config)).Applied);
        var second = _mqtt.Client("Second").ValueOrThrow();
        Assert.True(second.IsConnected);

        config = Config();
        config["connections"]!.AsObject().Remove("Second");
        Assert.True((await UpdateAsync(config)).Applied);
        Assert.Equal(MqttErrorCodes.ConnectionNotFound, _mqtt.Client("Second").Error!.Code);
        Assert.Equal(MqttErrorCodes.Disposed, (await second.PublishAsync("x/y", "z")).Error!.Code);
    }

    [Fact]
    public async Task Broker_user_changes_apply_without_a_restart()
    {
        var config = Config();
        config["broker"]!["allowAnonymous"] = false;
        config["broker"]!["users"] = new JsonArray(new JsonObject { ["username"] = "app", ["password"] = "pw" });
        Assert.True((await UpdateAsync(config)).Applied);

        // The running anonymous client keeps its connection; a new anonymous one is refused.
        Assert.True(_mqtt.Client().ValueOrThrow().IsConnected);
        await using var stranger = _mqtt.CreateClient("stranger", new MqttConnectionConfig
        {
            Host = "127.0.0.1", Port = _port, AutoReconnect = false, TimeoutSeconds = 5
        });
        Assert.Equal(MqttErrorCodes.AuthenticationFailed, (await stranger.ConnectAsync()).Error!.Code);
    }

    [Fact]
    public async Task Invalid_changes_are_ignored()
    {
        var client = _mqtt.Client().ValueOrThrow();
        var config = Config();
        config["connections"]!["Default"]!["port"] = 0;
        await UpdateAsync(config);

        Assert.Same(client, _mqtt.Client().ValueOrThrow());
        Assert.Equal(_port, client.Config.Port);
        Assert.True(client.IsConnected);
    }
}
