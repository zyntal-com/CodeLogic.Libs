using System.Net;
using System.Net.Sockets;
using CL.Mqtt.Events;
using CL.Mqtt.Models;
using CL.Mqtt.Services;
using Xunit;

namespace Mqtt.Tests;

/// <summary>Clients against the embedded broker over real loopback TCP. No external broker or container needed.</summary>
public sealed class BrokerClientIntegrationTests
{
    [Fact]
    public async Task Publish_reaches_matching_subscribers_only()
    {
        await using var broker = await TestBroker.StartAsync();
        var subscriber = broker.Client("sub");
        var publisher = broker.Client("pub");
        (await subscriber.ConnectAsync()).ThrowIfFailure();
        (await publisher.ConnectAsync()).ThrowIfFailure();

        var kitchen = new Inbox();
        var all = new Inbox();
        (await subscriber.SubscribeAsync("home/kitchen/+", kitchen.Handle)).ValueOrThrow();
        (await subscriber.SubscribeAsync("home/#", all.Handle)).ValueOrThrow();

        (await publisher.PublishAsync("home/kitchen/temperature", "21.5")).ThrowIfFailure();
        (await publisher.PublishAsync("home/hall/temperature", "19")).ThrowIfFailure();

        var everything = await all.WaitForAsync(2);
        var onlyKitchen = await kitchen.WaitForAsync(1);
        await kitchen.ExpectNoMoreThanAsync(1);
        Assert.Equal("21.5", onlyKitchen[0].PayloadAsString());
        Assert.Equal(["home/kitchen/temperature", "home/hall/temperature"], everything.Select(m => m.Topic));
    }

    [Fact]
    public async Task Handlers_can_share_a_filter_and_unsubscribe_independently()
    {
        await using var broker = await TestBroker.StartAsync();
        var client = broker.Client();
        (await client.ConnectAsync()).ThrowIfFailure();
        var first = new Inbox();
        var second = new Inbox();
        var a = (await client.SubscribeAsync("t/#", first.Handle)).ValueOrThrow();
        (await client.SubscribeAsync("t/#", second.Handle)).ValueOrThrow();

        (await client.PublishAsync("t/1", "x")).ThrowIfFailure();
        await first.WaitForAsync(1);
        await second.WaitForAsync(1);

        await a.DisposeAsync();
        Assert.False(a.IsActive);
        (await client.PublishAsync("t/2", "y")).ThrowIfFailure();
        await second.WaitForAsync(2);
        await first.ExpectNoMoreThanAsync(1);
        Assert.Equal(1, client.SubscriptionCount);
    }

    [Fact]
    public async Task Json_and_mqtt5_properties_round_trip()
    {
        await using var broker = await TestBroker.StartAsync();
        var client = broker.Client(configure: c => c.Protocol = MqttProtocol.V500);
        (await client.ConnectAsync()).ThrowIfFailure();
        var inbox = new Inbox();
        (await client.SubscribeAsync("json/#", inbox.Handle)).ValueOrThrow();

        var options = new MqttPublishOptions { UserProperties = [new("source", "test")], ResponseTopic = "json/reply" };
        (await client.PublishJsonAsync("json/reading", new MqttMessageTests.Reading("kitchen", 21.5), options: options)).ThrowIfFailure();

        var message = (await inbox.WaitForAsync(1))[0];
        Assert.Equal("application/json", message.ContentType);
        Assert.Equal("json/reply", message.ResponseTopic);
        Assert.Equal(new KeyValuePair<string, string>("source", "test"), Assert.Single(message.UserProperties));
        Assert.Equal(21.5, message.ReadJson<MqttMessageTests.Reading>().ValueOrThrow().Value);
    }

    [Fact]
    public async Task Retained_messages_reach_late_subscribers()
    {
        await using var broker = await TestBroker.StartAsync();
        var publisher = broker.Client("pub");
        (await publisher.ConnectAsync()).ThrowIfFailure();
        (await publisher.PublishAsync("state/lamp", "on", MqttPublishOptions.Retained)).ThrowIfFailure();

        var late = broker.Client("late");
        (await late.ConnectAsync()).ThrowIfFailure();
        var inbox = new Inbox();
        (await late.SubscribeAsync("state/#", inbox.Handle)).ValueOrThrow();

        var message = (await inbox.WaitForAsync(1))[0];
        Assert.True(message.Retain);
        Assert.Equal("on", message.PayloadAsString());
    }

    [Fact]
    public async Task Subscriptions_made_before_connecting_are_sent_on_connect()
    {
        await using var broker = await TestBroker.StartAsync();
        var client = broker.Client();
        var inbox = new Inbox();
        (await client.SubscribeAsync("early/#", inbox.Handle)).ValueOrThrow();
        Assert.Equal(MqttErrorCodes.NotConnected, (await client.PublishAsync("early/x", "1")).Error!.Code);

        (await client.ConnectAsync()).ThrowIfFailure();
        (await client.PublishAsync("early/x", "1")).ThrowIfFailure();
        await inbox.WaitForAsync(1);
    }

    [Fact]
    public async Task Bad_credentials_are_reported()
    {
        await using var broker = await TestBroker.StartAsync(c =>
        {
            c.AllowAnonymous = false;
            c.Users = [new() { Username = "app", Password = "right" }];
        });
        var rejected = new List<MqttBrokerConnectionRejectedEvent>();
        using var sub = broker.Events.Subscribe<MqttBrokerConnectionRejectedEvent>(rejected.Add);

        var wrong = broker.Client("wrong", c => { c.Username = "app"; c.Password = "wrong"; c.AutoReconnect = false; });
        var anonymous = broker.Client("anon", c => c.AutoReconnect = false);
        var right = broker.Client("right", c => { c.Username = "app"; c.Password = "right"; });

        Assert.Equal(MqttErrorCodes.AuthenticationFailed, (await wrong.ConnectAsync()).Error!.Code);
        Assert.Equal(MqttErrorCodes.AuthenticationFailed, (await anonymous.ConnectAsync()).Error!.Code);
        (await right.ConnectAsync()).ThrowIfFailure();
        Assert.Equal(MqttConnectionState.Disconnected, wrong.State);
        Assert.Equal(2, rejected.Count);
    }

    [Fact]
    public async Task Topic_permissions_drop_publishes_and_refuse_subscriptions()
    {
        await using var broker = await TestBroker.StartAsync(c =>
        {
            c.AllowAnonymous = false;
            c.Users =
            [
                new() { Username = "sensor", Password = "p", PublishTopics = ["sensors/#"], SubscribeTopics = ["commands/#"] },
                new() { Username = "admin", Password = "p" }
            ];
        });
        var sensor = broker.Client("sensor", c => { c.Username = "sensor"; c.Password = "p"; });
        var admin = broker.Client("admin", c => { c.Username = "admin"; c.Password = "p"; });
        (await sensor.ConnectAsync()).ThrowIfFailure();
        (await admin.ConnectAsync()).ThrowIfFailure();
        var seen = new Inbox();
        (await admin.SubscribeAsync("#", seen.Handle)).ValueOrThrow();

        var refused = await sensor.SubscribeAsync("secrets/#", (_, _) => Task.CompletedTask);
        Assert.Equal(MqttErrorCodes.SubscribeRejected, refused.Error!.Code);
        Assert.Equal(0, sensor.SubscriptionCount);

        (await sensor.PublishAsync("admin/reboot", "now")).ThrowIfFailure();     // MQTT 3.1.1: dropped silently
        (await sensor.PublishAsync("sensors/temp", "20")).ThrowIfFailure();
        var messages = await seen.WaitForAsync(1);
        await seen.ExpectNoMoreThanAsync(1);
        Assert.Equal("sensors/temp", messages[0].Topic);
    }

    [Fact]
    public async Task Mqtt5_publishers_learn_that_a_publish_was_refused()
    {
        await using var broker = await TestBroker.StartAsync(c =>
        {
            c.AllowAnonymous = false;
            c.Users = [new() { Username = "sensor", Password = "p", PublishTopics = ["sensors/#"] }];
        });
        var sensor = broker.Client("sensor", c => { c.Username = "sensor"; c.Password = "p"; c.Protocol = MqttProtocol.V500; });
        (await sensor.ConnectAsync()).ThrowIfFailure();

        var refused = await sensor.PublishAsync("admin/reboot", "now");
        Assert.Equal(MqttErrorCodes.PublishRejected, refused.Error!.Code);
        Assert.Equal("NotAuthorized", refused.Error.Details);
        (await sensor.PublishAsync("sensors/temp", "20")).ThrowIfFailure();
    }

    [Fact]
    public async Task Taps_see_client_publishes_and_the_broker_can_publish()
    {
        await using var broker = await TestBroker.StartAsync();
        var tapped = new Inbox();
        using var tap = broker.Broker.Tap("devices/#", tapped.Handle).ValueOrThrow();
        var client = broker.Client("device");
        (await client.ConnectAsync()).ThrowIfFailure();
        var inbox = new Inbox();
        (await client.SubscribeAsync("commands/#", inbox.Handle)).ValueOrThrow();

        (await client.PublishAsync("devices/lamp/state", "on")).ThrowIfFailure();
        var seen = (await tapped.WaitForAsync(1))[0];
        Assert.Equal(client.ClientId, seen.SenderClientId);

        (await broker.Broker.PublishAsync("commands/lamp", "off")).ThrowIfFailure();
        Assert.Equal("off", (await inbox.WaitForAsync(1))[0].PayloadAsString());
        await tapped.ExpectNoMoreThanAsync(1);

        var clients = (await broker.Broker.GetClientsAsync()).ValueOrThrow();
        Assert.Contains(clients, c => c.ClientId == client.ClientId);
        Assert.Equal(MqttErrorCodes.InvalidFilter, broker.Broker.Tap("bad/#/filter", tapped.Handle).Error!.Code);
    }

    [Fact]
    public async Task Client_reconnects_and_resubscribes_after_the_broker_restarts()
    {
        var port = TestBroker.FreePort();
        var broker = await TestBroker.StartAsync(port: port);
        var client = broker.Client();
        var connected = new List<MqttConnectedEvent>();
        using var events = broker.Events.Subscribe<MqttConnectedEvent>(connected.Add);
        (await client.ConnectAsync()).ThrowIfFailure();
        var inbox = new Inbox();
        (await client.SubscribeAsync("after/#", inbox.Handle)).ValueOrThrow();

        await broker.Broker.StopAsync();
        await WaitUntilAsync(() => client.State == MqttConnectionState.Reconnecting);
        (await broker.Broker.StartAsync()).ThrowIfFailure();
        // State turns Connected only after the subscriptions were sent again; IsConnected is true slightly earlier.
        await WaitUntilAsync(() => client.State == MqttConnectionState.Connected, seconds: 15);

        (await broker.Broker.PublishAsync("after/restart", "hello")).ThrowIfFailure();
        Assert.Equal("hello", (await inbox.WaitForAsync(1))[0].PayloadAsString());
        Assert.Equal([false, true], connected.Select(e => e.IsReconnect));
        await broker.DisposeAsync();
    }

    [Fact]
    public async Task Retained_messages_survive_a_broker_restart_when_persisted()
    {
        var file = Path.Combine(Path.GetTempPath(), $"cl-mqtt-retained-{Guid.NewGuid():N}.json");
        try
        {
            var port = TestBroker.FreePort();
            await using (var first = await TestBroker.StartAsync(retainedFile: file, port: port))
            {
                var publisher = first.Client();
                (await publisher.ConnectAsync()).ThrowIfFailure();
                (await publisher.PublishAsync("persist/me", "kept", MqttPublishOptions.Retained)).ThrowIfFailure();
                await WaitUntilAsync(async () => (await first.Broker.GetClientsAsync()).Value!.Count == 1);
            }
            Assert.True(File.Exists(file));

            await using var second = await TestBroker.StartAsync(retainedFile: file, port: port);
            var reader = second.Client();
            (await reader.ConnectAsync()).ThrowIfFailure();
            var inbox = new Inbox();
            (await reader.SubscribeAsync("persist/#", inbox.Handle)).ValueOrThrow();
            Assert.Equal("kept", (await inbox.WaitForAsync(1))[0].PayloadAsString());
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Loopback_broker_does_not_listen_on_ipv6()
    {
        if (!Socket.OSSupportsIPv6)
            return;
        await using var broker = await TestBroker.StartAsync();
        using var probe = new TcpClient(AddressFamily.InterNetworkV6);
        await Assert.ThrowsAnyAsync<SocketException>(() => probe.ConnectAsync(IPAddress.IPv6Loopback, broker.Port));
    }

    [Fact]
    public async Task Invalid_input_and_unreachable_brokers_return_errors()
    {
        var client = new MqttClientService("nowhere", new MqttConnectionConfig
        {
            Host = "127.0.0.1",
            Port = TestBroker.FreePort(),
            AutoReconnect = false,
            TimeoutSeconds = 2
        }, CodeLogic.Core.Logging.NullLogger.Instance);
        await using (client)
        {
            // A refused loopback connect fails at once on Linux but takes about 2 s on Windows (SYN retries): either code is right.
            Assert.Contains((await client.ConnectAsync()).Error!.Code, new[] { MqttErrorCodes.ConnectFailed, MqttErrorCodes.Timeout });
            Assert.Equal(MqttErrorCodes.InvalidTopic, (await client.PublishAsync("a/+/b", "x")).Error!.Code);
            Assert.Equal(MqttErrorCodes.InvalidFilter, (await client.SubscribeAsync("a/#/b", (_, _) => Task.CompletedTask)).Error!.Code);
            Assert.Equal(MqttErrorCodes.NotConnected, (await client.PublishAsync("a/b", "x")).Error!.Code);
        }
        Assert.Equal(MqttErrorCodes.Disposed, (await client.ConnectAsync()).Error!.Code);
    }

    [Fact]
    public void Reconnect_delay_grows_and_is_capped()
    {
        var client = new MqttClientService("delay", new MqttConnectionConfig { ReconnectMinDelaySeconds = 1, ReconnectMaxDelaySeconds = 30 },
            CodeLogic.Core.Logging.NullLogger.Instance);
        Assert.InRange(client.ReconnectDelay(0).TotalSeconds, 0.8, 1.2);
        Assert.InRange(client.ReconnectDelay(3).TotalSeconds, 6.4, 9.6);
        Assert.InRange(client.ReconnectDelay(50).TotalSeconds, 24, 36);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not met in time.");
            await Task.Delay(50);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not met in time.");
            await Task.Delay(50);
        }
    }
}
