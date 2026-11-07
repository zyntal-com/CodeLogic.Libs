using CL.Mqtt.Models;
using CL.Mqtt.Services;
using CodeLogic.Core.Logging;
using MQTTnet;
using Xunit;

namespace Mqtt.Tests;

public sealed class AuditCompletionTests
{
    [Fact]
    public async Task A_queued_sweeper_can_start_after_stop_has_cleared_its_source()
    {
        Func<Task>? queued = null;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var broker = new MqttBrokerService(new MqttBrokerConfig { Port = TestBroker.FreePort() }, NullLogger.Instance)
        {
            SweepScheduler = work => { queued = work; return completion.Task; }
        };
        (await broker.StartAsync()).ThrowIfFailure();
        var stopping = broker.StopAsync();
        try
        {
            Assert.NotNull(queued);
            Assert.False(stopping.IsCompleted);
            try { await queued(); completion.TrySetResult(); }
            catch (Exception ex) { completion.TrySetException(ex); }
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { completion.TrySetResult(); await stopping; }
    }

    [Fact]
    public async Task A_faulted_sweeper_does_not_leak_the_broker_listener()
    {
        var port = TestBroker.FreePort();
        await using var broker = new MqttBrokerService(new MqttBrokerConfig { Port = port }, NullLogger.Instance)
        {
            SweepScheduler = _ => Task.FromException(new InvalidOperationException("sweep failed"))
        };
        (await broker.StartAsync()).ThrowIfFailure();
        await Assert.ThrowsAsync<InvalidOperationException>(broker.StopAsync);
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
        listener.Start();
    }

    [Fact]
    public async Task Concurrent_network_connections_share_one_atomic_quota()
    {
        await using var broker = await TestBroker.StartAsync(c => c.MaxSessionsPerUser = 1);
        broker.Broker.Authorizer = new BarrierAuthorizer(16);
        var clients = Enumerable.Range(0, 16).Select(_ => new MqttClientFactory().CreateMqttClient()).ToArray();
        try
        {
            var results = await Task.WhenAll(clients.Select(async (client, i) =>
            {
                try
                {
                    var result = await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", broker.Port)
                        .WithClientId("quota-" + i).WithCredentials("same-user", "p").Build());
                    return result.ResultCode == MqttClientConnectResultCode.Success;
                }
                catch (MQTTnet.Adapter.MqttConnectingFailedException) { return false; }
            }));
            Assert.Single(results, r => r);
        }
        finally { foreach (var client in clients) client.Dispose(); }
    }

    [Fact]
    public async Task Different_users_cannot_reserve_one_id_concurrently()
    {
        await using var broker = await TestBroker.StartAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(i => Task.Run(() =>
            broker.Broker.TryReserveSession("one-id", "user" + i, "endpoint" + i, DateTimeOffset.UtcNow, out _, out _))));
        Assert.Single(results, r => r);
    }

    [Fact]
    public async Task Sweep_preserves_a_connection_between_validation_and_session_creation()
    {
        await using var broker = await TestBroker.StartAsync();
        Assert.True(broker.Broker.TryReserveSession("pending", "alice", "endpoint", DateTimeOffset.UtcNow, out _, out _));
        await broker.Broker.SweepSessionsAsync(DateTimeOffset.UtcNow);
        Assert.False(broker.Broker.TryReserveSession("pending", "bob", "endpoint2", DateTimeOffset.UtcNow, out _, out _));
        // A failed handshake cannot hold quota forever after MQTTnet's 15-second CONNECT timeout.
        await broker.Broker.SweepSessionsAsync(DateTimeOffset.UtcNow.AddMinutes(2));
        Assert.True(broker.Broker.TryReserveSession("pending", "bob", "endpoint2", DateTimeOffset.UtcNow, out _, out _));
    }

    [Fact]
    public void Plain_listener_warning_is_required_even_with_TLS_enabled()
    {
        var config = new MqttBrokerConfig { BindAddress = "0.0.0.0", Tls = new() { Enabled = true } };
        Assert.True(MqttBrokerService.HasNetworkPlainListener(config));
        config.Tls.DisablePlainPort = true;
        Assert.False(MqttBrokerService.HasNetworkPlainListener(config));
        config.Tls.Enabled = false;
        config.BindAddress = "127.0.0.1";
        Assert.False(MqttBrokerService.HasNetworkPlainListener(config));
    }

    [Fact]
    public async Task A_failed_replacement_handshake_restores_the_connected_owner()
    {
        await using var broker = await TestBroker.StartAsync();
        var client = broker.Client("original", c => c.ClientId = "held-id");
        (await client.ConnectAsync()).ThrowIfFailure();
        Assert.True(broker.Broker.TryReserveSession("held-id", null, "failed-replacement", DateTimeOffset.UtcNow, out _, out _));
        await broker.Broker.SweepSessionsAsync(DateTimeOffset.UtcNow.AddMinutes(2));
        Assert.True(client.IsConnected);
        Assert.True(broker.Broker.TryReserveSession("held-id", null, "another-replacement", DateTimeOffset.UtcNow, out _, out _));
        Assert.False(broker.Broker.TryReserveSession("held-id", "another-user", "foreign", DateTimeOffset.UtcNow, out _, out _));
    }

    [Fact]
    public async Task Session_deletion_keeps_admission_blocked_until_the_delete_finishes()
    {
        await using var broker = await TestBroker.StartAsync(c => c.PersistentSessionExpiryMinutes = 1);
        var client = broker.Client("persistent", c => { c.ClientId = "expiring"; c.CleanSession = false; });
        (await client.ConnectAsync()).ThrowIfFailure();
        (await client.DisconnectAsync()).ThrowIfFailure();
        for (var i = 0; i < 100 && (await broker.Broker.RawServer!.GetClientsAsync()).Count > 0; i++)
            await Task.Delay(10);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        broker.Broker.RawServer!.SessionDeletedAsync += async _ => { entered.TrySetResult(); await release.Task; };
        var sweep = broker.Broker.SweepSessionsAsync(DateTimeOffset.UtcNow.AddMinutes(2));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(broker.Broker.TryReserveSession("expiring", null, "new", DateTimeOffset.UtcNow, out _, out _));
        }
        finally { release.TrySetResult(); await sweep; }
        Assert.True(broker.Broker.TryReserveSession("expiring", "new-user", "new", DateTimeOffset.UtcNow, out _, out _));
    }

    [Fact]
    public async Task Authentication_refusal_stops_automatic_reconnect()
    {
        await using var broker = await TestBroker.StartAsync(c => c.AllowAnonymous = false);
        var client = broker.Client("bad", c => { c.Username = "unknown"; c.Password = "bad"; c.AutoReconnect = true; });
        Assert.Equal(MqttErrorCodes.AuthenticationFailed, (await client.ConnectAsync()).Error!.Code);
        Assert.Equal(MqttConnectionState.Disconnected, client.State);
        Assert.Equal(1, client.FailedAttempts);
    }

    [Fact]
    public async Task Retained_snapshot_arriving_during_write_is_persisted_by_the_next_round()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mqtt-writer-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "retained.json");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var store = new RetainedMessageStore(path, NullLogger.Instance, async () =>
        {
            if (Interlocked.Increment(ref calls) == 1) { entered.SetResult(); await release.Task; }
        });
        try
        {
            store.Schedule([new MqttApplicationMessageBuilder().WithTopic("old").WithPayload("old").Build()]);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            store.Schedule([new MqttApplicationMessageBuilder().WithTopic("latest").WithPayload("latest").Build()]);
            release.SetResult();
            await store.DisposeAsync();
            Assert.Equal(2, calls);
            Assert.Equal("latest", Assert.Single(await new RetainedMessageStore(path, NullLogger.Instance).LoadAsync(default)).Topic);
        }
        finally
        {
            release.TrySetResult();
            await store.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class BarrierAuthorizer(int count) : IMqttBrokerAuthorizer
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;
        public async ValueTask<bool> AuthenticateAsync(MqttBrokerConnectRequest request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _arrived) == count) _ready.SetResult();
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return true;
        }
        public bool CanPublish(string? username, string clientId, string topic) => true;
        public bool CanSubscribe(string? username, string clientId, string filter) => true;
    }
}
