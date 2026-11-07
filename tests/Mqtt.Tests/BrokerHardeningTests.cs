using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CL.Mqtt.Events;
using CL.Mqtt.Models;
using CL.Mqtt.Services;
using CodeLogic.Core.Logging;
using MQTTnet;
using Xunit;

namespace Mqtt.Tests;

/// <summary>The broker's own protections: reserved ids, session ownership and limits, retained limits, TLS, will authorization.</summary>
public sealed class BrokerHardeningTests
{
    private static readonly Action<MqttBrokerConfig> TwoUsers = c =>
    {
        c.AllowAnonymous = false;
        c.Users =
        [
            new() { Username = "alice", Password = "a", SubscribeTopics = ["alice/#"], PublishTopics = ["alice/#"] },
            new() { Username = "bob", Password = "b", SubscribeTopics = ["bob/#"], PublishTopics = ["bob/#"] },
            new() { Username = "admin", Password = "p" }
        ];
    };

    [Theory]
    [InlineData("$broker")]
    [InlineData("$SYS")]
    public async Task Reserved_client_ids_are_refused(string clientId)
    {
        await using var broker = await TestBroker.StartAsync();
        var rejected = new List<MqttBrokerConnectionRejectedEvent>();
        using var sub = broker.Events.Subscribe<MqttBrokerConnectionRejectedEvent>(rejected.Add);

        var impostor = broker.Client("impostor", c => { c.ClientId = clientId; c.AutoReconnect = false; });
        var result = await impostor.ConnectAsync();

        Assert.Equal(MqttErrorCodes.ConnectFailed, result.Error!.Code);
        Assert.Equal("ClientIdentifierNotValid", result.Error.Details);
        Assert.Equal("reserved client id", Assert.Single(rejected).Reason);
    }

    [Fact]
    public async Task A_client_cannot_publish_as_the_broker_even_through_the_raw_client()
    {
        await using var broker = await TestBroker.StartAsync(c =>
        {
            c.AllowAnonymous = false;
            c.Users = [new() { Username = "sensor", Password = "p", PublishTopics = ["sensors/#"] }];
        });
        using var raw = new MqttClientFactory().CreateMqttClient();
        var options = new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", broker.Port).WithClientId("$broker").WithCredentials("sensor", "p").Build();
        try
        {
            var result = await raw.ConnectAsync(options);
            Assert.Equal(MqttClientConnectResultCode.ClientIdentifierNotValid, result.ResultCode);
        }
        catch (MQTTnet.Adapter.MqttConnectingFailedException)
        {
            // Also fine: refused.
        }
        Assert.False(raw.IsConnected);
    }

    [Fact]
    public async Task Another_user_cannot_take_over_a_client_id_but_the_same_user_can()
    {
        await using var broker = await TestBroker.StartAsync(TwoUsers);
        var rejected = new List<MqttBrokerConnectionRejectedEvent>();
        using var sub = broker.Events.Subscribe<MqttBrokerConnectionRejectedEvent>(rejected.Add);

        var alice = broker.Client("alice", c => { c.ClientId = "shared-id"; c.Username = "alice"; c.Password = "a"; });
        (await alice.ConnectAsync()).ThrowIfFailure();

        var bob = broker.Client("bob", c => { c.ClientId = "shared-id"; c.Username = "bob"; c.Password = "b"; c.AutoReconnect = false; });
        var refused = await bob.ConnectAsync();
        Assert.Equal(MqttErrorCodes.ConnectFailed, refused.Error!.Code);
        Assert.Equal("ClientIdentifierNotValid", refused.Error.Details);
        Assert.Contains("held by user 'alice'", Assert.Single(rejected).Reason, StringComparison.Ordinal);
        Assert.True(alice.IsConnected);   // and alice was not kicked

        // The same user may reconnect with its id (a device coming back after a crash).
        var aliceAgain = broker.Client("alice2", c => { c.ClientId = "shared-id"; c.Username = "alice"; c.Password = "a"; c.AutoReconnect = false; });
        (await aliceAgain.ConnectAsync()).ThrowIfFailure();
    }

    [Fact]
    public async Task A_persistent_session_cannot_be_resumed_by_another_user()
    {
        await using var broker = await TestBroker.StartAsync(TwoUsers);
        var alice = broker.Client("alice", c => { c.ClientId = "alice-device"; c.Username = "alice"; c.Password = "a"; c.CleanSession = false; });
        (await alice.ConnectAsync()).ThrowIfFailure();
        (await alice.SubscribeAsync("alice/#", (_, _) => Task.CompletedTask)).ValueOrThrow();
        (await alice.DisconnectAsync()).ThrowIfFailure();

        // Bob knows alice's client id and tries to resume her session (and inherit her subscriptions).
        var bob = broker.Client("bob", c => { c.ClientId = "alice-device"; c.Username = "bob"; c.Password = "b"; c.CleanSession = false; c.AutoReconnect = false; });
        var refused = await bob.ConnectAsync();
        Assert.Equal(MqttErrorCodes.ConnectFailed, refused.Error!.Code);
        Assert.Equal("ClientIdentifierNotValid", refused.Error.Details);
    }

    [Fact]
    public async Task Sessions_per_user_are_capped()
    {
        await using var broker = await TestBroker.StartAsync(c => { TwoUsers(c); c.MaxSessionsPerUser = 2; });
        var one = broker.Client("one", c => { c.Username = "alice"; c.Password = "a"; });
        var two = broker.Client("two", c => { c.Username = "alice"; c.Password = "a"; });
        var three = broker.Client("three", c => { c.Username = "alice"; c.Password = "a"; c.AutoReconnect = false; c.Protocol = MqttProtocol.V500; });
        var old = broker.Client("old", c => { c.Username = "alice"; c.Password = "a"; c.AutoReconnect = false; });
        (await one.ConnectAsync()).ThrowIfFailure();
        (await two.ConnectAsync()).ThrowIfFailure();

        var refused = await three.ConnectAsync();
        Assert.Equal(MqttErrorCodes.ConnectFailed, refused.Error!.Code);
        Assert.Equal("QuotaExceeded", refused.Error.Details);
        Assert.Equal("ServerUnavailable", (await old.ConnectAsync()).Error!.Details);   // MQTT 3.1.1 has no quota code

        // Another user is not affected by alice's limit.
        var bob = broker.Client("bob", c => { c.Username = "bob"; c.Password = "b"; });
        (await bob.ConnectAsync()).ThrowIfFailure();
    }

    [Fact]
    public async Task Idle_persistent_sessions_expire_and_free_their_id()
    {
        await using var broker = await TestBroker.StartAsync(c => { TwoUsers(c); c.PersistentSessionExpiryMinutes = 60; });
        var alice = broker.Client("alice", c => { c.ClientId = "sleepy"; c.Username = "alice"; c.Password = "a"; c.CleanSession = false; });
        (await alice.ConnectAsync()).ThrowIfFailure();
        (await alice.DisconnectAsync()).ThrowIfFailure();
        await WaitUntilAsync(async () => (await broker.Broker.RawServer!.GetClientsAsync()).Count == 0);
        Assert.Contains(await broker.Broker.RawServer!.GetSessionsAsync(), s => s.Id == "sleepy");

        await broker.Broker.SweepSessionsAsync(DateTimeOffset.UtcNow);                    // not expired yet
        Assert.Contains(await broker.Broker.RawServer!.GetSessionsAsync(), s => s.Id == "sleepy");

        await broker.Broker.SweepSessionsAsync(DateTimeOffset.UtcNow.AddHours(2));        // expired
        Assert.DoesNotContain(await broker.Broker.RawServer!.GetSessionsAsync(), s => s.Id == "sleepy");

        // The id is free again, for anyone.
        var bob = broker.Client("bob", c => { c.ClientId = "sleepy"; c.Username = "bob"; c.Password = "b"; c.AutoReconnect = false; });
        (await bob.ConnectAsync()).ThrowIfFailure();
    }

    [Fact]
    public async Task Retained_messages_are_capped_by_count_and_payload_size()
    {
        await using var broker = await TestBroker.StartAsync(c => { c.MaxRetainedMessages = 2; c.MaxRetainedPayloadBytes = 10; });
        var client = broker.Client("device", c => c.Protocol = MqttProtocol.V500);
        (await client.ConnectAsync()).ThrowIfFailure();
        var server = broker.Broker.RawServer!;

        (await client.PublishAsync("r/1", "one", MqttPublishOptions.Retained)).ThrowIfFailure();
        (await client.PublishAsync("r/2", "two", MqttPublishOptions.Retained)).ThrowIfFailure();
        await WaitUntilAsync(async () => (await server.GetRetainedMessagesAsync()).Count == 2);

        var third = await client.PublishAsync("r/3", "three", MqttPublishOptions.Retained);
        Assert.Equal(MqttErrorCodes.PublishRejected, third.Error!.Code);
        Assert.Equal("QuotaExceeded", third.Error.Details);

        (await client.PublishAsync("r/1", "update", MqttPublishOptions.Retained)).ThrowIfFailure();   // existing topics may change
        var big = await client.PublishAsync("r/1", new string('x', 11), MqttPublishOptions.Retained);
        Assert.Equal("QuotaExceeded", big.Error!.Details);
        (await client.PublishAsync("r/4", "not retained at all", MqttPublishOptions.Default)).ThrowIfFailure();

        (await client.PublishAsync("r/1", "", MqttPublishOptions.Retained)).ThrowIfFailure();         // a clear always passes
        await WaitUntilAsync(async () => (await server.GetRetainedMessagesAsync()).Count == 1);
        (await client.PublishAsync("r/3", "three", MqttPublishOptions.Retained)).ThrowIfFailure();
    }

    [Fact]
    public async Task A_will_message_goes_through_the_authorizer()
    {
        await using var broker = await TestBroker.StartAsync(c =>
        {
            c.AllowAnonymous = false;
            c.Users =
            [
                new() { Username = "sensor", Password = "p", PublishTopics = ["sensors/#"] },
                new() { Username = "admin", Password = "p" }
            ];
        });
        var admin = broker.Client("admin", c => { c.Username = "admin"; c.Password = "p"; });
        (await admin.ConnectAsync()).ThrowIfFailure();
        var inbox = new Inbox();
        (await admin.SubscribeAsync("#", inbox.Handle)).ValueOrThrow();

        // Vanish without DISCONNECT so the broker publishes the will: once to a forbidden topic, once to an allowed one.
        await VanishWithWillAsync(broker.Port, "sensor", "admin/reboot");
        await inbox.ExpectNoMoreThanAsync(0, 1000);
        await VanishWithWillAsync(broker.Port, "sensor", "sensors/gone");
        Assert.Equal("sensors/gone", (await inbox.WaitForAsync(1))[0].Topic);
    }

    [Theory]
    [InlineData(MqttProtocol.V311)]
    [InlineData(MqttProtocol.V500)]
    public async Task Persistent_sessions_queue_messages_while_the_client_is_away(MqttProtocol protocol)
    {
        await using var broker = await TestBroker.StartAsync();
        var sleeper = broker.Client("sleeper", c => { c.ClientId = $"sleeper-{protocol}"; c.CleanSession = false; c.Protocol = protocol; c.SessionExpirySeconds = 300; });
        (await sleeper.ConnectAsync()).ThrowIfFailure();
        var inbox = new Inbox();
        (await sleeper.SubscribeAsync("queued/#", inbox.Handle)).ValueOrThrow();
        (await sleeper.DisconnectAsync()).ThrowIfFailure();
        await WaitUntilAsync(async () => (await broker.Broker.RawServer!.GetClientsAsync()).Count == 0);

        (await broker.Broker.PublishAsync("queued/while-away", "kept")).ThrowIfFailure();
        (await sleeper.ConnectAsync()).ThrowIfFailure();
        Assert.Equal("kept", (await inbox.WaitForAsync(1))[0].PayloadAsString());
    }

    [Fact]
    public async Task Hashed_passwords_pinned_client_ids_and_publish_flags_work()
    {
        await using var broker = await TestBroker.StartAsync(c =>
        {
            c.AllowAnonymous = false;
            c.Users =
            [
                new() { Username = "device", Password = MqttBrokerUser.HashPassword("pw"), ClientIds = ["device-1"] },
                new() { Username = "viewer", Password = "v", CanPublish = false }
            ];
        });
        var wrongId = broker.Client("x", c => { c.ClientId = "device-2"; c.Username = "device"; c.Password = "pw"; c.AutoReconnect = false; });
        Assert.Equal(MqttErrorCodes.AuthenticationFailed, (await wrongId.ConnectAsync()).Error!.Code);
        var wrongPw = broker.Client("y", c => { c.ClientId = "device-1"; c.Username = "device"; c.Password = "pw!"; c.AutoReconnect = false; });
        Assert.Equal(MqttErrorCodes.AuthenticationFailed, (await wrongPw.ConnectAsync()).Error!.Code);
        var device = broker.Client("z", c => { c.ClientId = "device-1"; c.Username = "device"; c.Password = "pw"; });
        (await device.ConnectAsync()).ThrowIfFailure();

        var viewer = broker.Client("viewer", c => { c.Username = "viewer"; c.Password = "v"; c.Protocol = MqttProtocol.V500; });
        (await viewer.ConnectAsync()).ThrowIfFailure();
        (await viewer.SubscribeAsync("#", (_, _) => Task.CompletedTask)).ValueOrThrow();
        Assert.Equal(MqttErrorCodes.PublishRejected, (await viewer.PublishAsync("any/topic", "x")).Error!.Code);
    }

    [Fact]
    public async Task Tls_listener_serves_clients_and_the_plain_port_can_be_closed()
    {
        var pfx = Path.Combine(Path.GetTempPath(), $"cl-mqtt-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(pfx, SelfSigned().Export(X509ContentType.Pkcs12, "pw"));
        try
        {
            var tlsPort = TestBroker.FreePort();
            await using var broker = await TestBroker.StartAsync(c =>
            {
                c.Tls = new MqttBrokerTlsConfig { Enabled = true, Port = tlsPort, CertificatePath = pfx, CertificatePassword = "pw", DisablePlainPort = true };
            });
            Assert.Contains($"tls 127.0.0.1:{tlsPort}", broker.Broker.Endpoint, StringComparison.Ordinal);
            var seen = new RecordingAuthorizer();
            broker.Broker.Authorizer = seen;

            var secure = broker.Client("secure", c => { c.Port = tlsPort; c.UseTls = true; c.AllowUntrustedCertificates = true; });
            (await secure.ConnectAsync()).ThrowIfFailure();
            Assert.True(Assert.Single(seen.Requests).IsSecureConnection);

            var plain = broker.Client("plain", c => { c.AutoReconnect = false; c.TimeoutSeconds = 2; });
            Assert.True((await plain.ConnectAsync()).IsFailure);
        }
        finally
        {
            File.Delete(pfx);
        }
    }

    [Fact]
    public async Task Tls_without_a_loadable_certificate_fails_the_start()
    {
        var config = new MqttBrokerConfig { Enabled = true, Port = TestBroker.FreePort(), AllowAnonymous = true,
            Tls = new MqttBrokerTlsConfig { Enabled = true, Port = TestBroker.FreePort(), CertificatePath = Path.Combine(Path.GetTempPath(), "missing.pfx") } };
        Assert.Contains(config.Validate("broker"), e => e.Path == "broker.tls.certificatePath");

        await using var broker = new MqttBrokerService(config, NullLogger.Instance);
        var started = await broker.StartAsync();
        Assert.Equal(MqttErrorCodes.BrokerStartFailed, started.Error!.Code);
    }

    [Fact]
    public void Refused_credentials_retry_at_the_longest_delay()
    {
        var client = new MqttClientService("auth", new MqttConnectionConfig { ReconnectMinDelaySeconds = 1, ReconnectMaxDelaySeconds = 30 }, NullLogger.Instance);
        Assert.InRange(client.MaxReconnectDelay().TotalSeconds, 24, 36);
        Assert.Equal(MQTTnet.MqttClientConnectResultCode.BadUserNameOrPassword, MqttClientService.ParseConnectResultCode("Connecting with MQTT server failed (BadUserNameOrPassword)."));
        Assert.Equal(MQTTnet.MqttClientConnectResultCode.NotAuthorized, MqttClientService.ParseConnectResultCode("... (NotAuthorized)."));
        Assert.Equal(MQTTnet.MqttClientConnectResultCode.UnspecifiedError, MqttClientService.ParseConnectResultCode("no code here"));
    }

    [Fact]
    public async Task Retained_store_writes_the_last_snapshot_once_per_round()
    {
        var file = Path.Combine(Path.GetTempPath(), $"cl-mqtt-store-{Guid.NewGuid():N}", "retained.json");
        try
        {
            var store = new RetainedMessageStore(file, NullLogger.Instance);
            for (var i = 1; i <= 5; i++)
                store.Schedule([new MqttApplicationMessageBuilder().WithTopic($"t/{i}").WithPayload($"v{i}").WithRetainFlag().Build()]);
            await store.DisposeAsync();

            var reloaded = await new RetainedMessageStore(file, NullLogger.Instance).LoadAsync(default);
            Assert.Equal("t/5", Assert.Single(reloaded).Topic);
        }
        finally
        {
            if (File.Exists(file)) Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }

    [Fact]
    public void Broker_config_validation_covers_the_new_rules()
    {
        var config = new MqttBrokerConfig
        {
            Enabled = true,
            Users =
            [
                new() { Username = "with space", Password = "p", PublishTopics = null!, SubscribeTopics = null!, ClientIds = null! },
                new() { Username = "$sys", Password = "p" },
                new() { Username = "badhash", Password = "$pbkdf2-sha256$nope" },
                new() { Username = "badid", Password = "p", ClientIds = ["$broker"] }
            ],
            Tls = new MqttBrokerTlsConfig { Enabled = true, Port = 1883 },
            MaxSessionsPerUser = -1,
            MaxRetainedMessages = 0
        };
        var errors = config.Validate("broker").Select(e => e.Path).ToList();
        Assert.Contains("broker.users[0].username", errors);
        Assert.Contains("broker.users[1].username", errors);
        Assert.Contains("broker.users[2].password", errors);
        Assert.Contains("broker.users[3].clientIds", errors);
        Assert.Contains("broker.tls.certificatePath", errors);
        Assert.Contains("broker.tls.port", errors);
        Assert.Contains("broker.maxSessionsPerUser", errors);
        Assert.Contains("broker.maxRetainedMessages", errors);
        Assert.Empty(config.Users[0].PublishTopics);   // null lists become empty, never NRE

        var connection = new MqttConnectionConfig { ClientId = "$mine", SessionExpirySeconds = 0 };
        var paths = connection.Validate("c").Select(e => e.Path).ToList();
        Assert.Contains("c.clientId", paths);
        Assert.Contains("c.sessionExpirySeconds", paths);
        Assert.True(MqttBrokerUser.IsHash(MqttBrokerUser.HashPassword("x")));
        Assert.True(MqttBrokerUser.PasswordMatches(MqttBrokerUser.HashPassword("x"), "x"));
        Assert.False(MqttBrokerUser.PasswordMatches(MqttBrokerUser.HashPassword("x"), "y"));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task VanishWithWillAsync(int port, string user, string willTopic)
    {
        var raw = new MqttClientFactory().CreateMqttClient();
        var options = new MqttClientOptionsBuilder()
            .WithTcpServer("127.0.0.1", port)
            .WithClientId($"will-{Guid.NewGuid():N}")
            .WithCredentials(user, "p")
            .WithWillTopic(willTopic)
            .WithWillPayload("offline")
            .Build();
        await raw.ConnectAsync(options);
        raw.Dispose();   // closes the socket without DISCONNECT
    }

    private static X509Certificate2 SelfSigned()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private sealed class RecordingAuthorizer : IMqttBrokerAuthorizer
    {
        public List<MqttBrokerConnectRequest> Requests { get; } = [];

        public ValueTask<bool> AuthenticateAsync(MqttBrokerConnectRequest request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request);
            return ValueTask.FromResult(true);
        }

        public bool CanPublish(string? username, string clientId, string topic) => true;

        public bool CanSubscribe(string? username, string clientId, string filter) => true;
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
