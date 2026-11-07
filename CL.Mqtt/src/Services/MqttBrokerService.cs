using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using CL.Mqtt.Events;
using CL.Mqtt.Models;
using CodeLogic.Core.Events;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;

namespace CL.Mqtt.Services;

/// <summary>
/// The embedded MQTT broker: an MQTT server inside the application process.
/// <para>
/// <b>Security.</b> Every connection, publish and subscription goes through <see cref="Authorizer"/>
/// (by default <see cref="ConfigBrokerAuthorizer"/>, the users in <c>config.mqtt.json</c>). On top of that the broker
/// itself refuses client ids starting with <c>$</c>, binds every client id to the user that first connected with it
/// (another user cannot take the session over), caps the sessions a user may hold and the retained messages it may
/// store, and expires persistent sessions nobody reconnects to. The default bind address is <c>127.0.0.1</c>, so only
/// local programs can connect until you open it up; do that with <see cref="MqttBrokerConfig.Tls"/> on.
/// </para>
/// <para>
/// <b>Taps</b> (<see cref="Tap"/>) let the application see messages clients publish without connecting a client
/// of its own; <see cref="PublishAsync(string, ReadOnlyMemory{byte}, MqttPublishOptions?, CancellationToken)"/>
/// publishes as the broker itself.
/// </para>
/// <para>
/// <b>Retained messages</b> are kept in a file when <see cref="MqttBrokerConfig.PersistRetainedMessages"/> is on and
/// a file path was given, so devices that publish their state retained are known again after a restart.
/// </para>
/// </summary>
public sealed partial class MqttBrokerService : IAsyncDisposable
{
    /// <summary>The client id messages published by the broker itself carry.</summary>
    public const string BrokerClientId = "$broker";

    /// <summary>How often persistent sessions are checked for expiry.</summary>
    internal static readonly TimeSpan SessionSweepInterval = TimeSpan.FromMinutes(1);

    /// <summary>How long a client has to send CONNECT after the TCP connection opened.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    private readonly ILogger _logger;
    private readonly IEventBus? _events;
    private readonly string? _retainedFile;
    private readonly Lock _lock = new();
    private readonly List<BrokerTap> _taps = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    // Identifies the broker's own injected messages in the publish interceptor, whatever client id they carry.
    private readonly IDictionary _brokerSessionItems = new Dictionary<object, object>();
    private readonly ConcurrentDictionary<string, SessionOwner> _sessions = new(StringComparer.Ordinal);

    private HashSet<string> _retainedTopics = new(StringComparer.Ordinal);
    private MqttServer? _server;
    private RetainedMessageStore? _retained;
    private X509Certificate2? _certificate;
    private CancellationTokenSource? _sweepCancel;
    private Task? _sweeper;
    private IMqttBrokerAuthorizer _authorizer;

    /// <summary>Creates the broker. It does not listen until <see cref="StartAsync"/>.</summary>
    /// <param name="config">Broker settings; validate them first (<see cref="MqttBrokerConfig.Validate"/>).</param>
    /// <param name="logger">Logger for start/stop, refused clients and tap failures.</param>
    /// <param name="events">Optional event bus for the broker events.</param>
    /// <param name="retainedFile">File for retained messages; null keeps them in memory only.</param>
    public MqttBrokerService(MqttBrokerConfig config, ILogger logger, IEventBus? events = null, string? retainedFile = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        Config = config;
        _logger = logger;
        _events = events;
        _retainedFile = retainedFile;
        _authorizer = new ConfigBrokerAuthorizer(config);
    }

    /// <summary>The settings in use.</summary>
    public MqttBrokerConfig Config { get; private set; }

    /// <summary>
    /// Decides who may connect and which topics each client may use. Replace it to use your own accounts; the
    /// change applies to new connections, publishes and subscriptions immediately.
    /// </summary>
    public IMqttBrokerAuthorizer Authorizer
    {
        get => Volatile.Read(ref _authorizer);
        set => Volatile.Write(ref _authorizer, value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <summary>True while the broker is listening.</summary>
    public bool IsRunning => _server?.IsStarted == true;

    /// <summary>The address and port(s) the broker listens on (or will listen on), e.g. <c>127.0.0.1:1883, tls 127.0.0.1:8883</c>.</summary>
    public string Endpoint
    {
        get
        {
            var plain = Config.Tls is { Enabled: true, DisablePlainPort: true } ? null : $"{Config.BindAddress}:{Config.Port}";
            var tls = Config.Tls.Enabled ? $"tls {Config.BindAddress}:{Config.Tls.Port}" : null;
            return string.Join(", ", new[] { plain, tls }.Where(e => e is not null));
        }
    }

    /// <summary>The underlying MQTTnet server while running, for features this wrapper does not cover.</summary>
    public MqttServer? RawServer => _server;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>Starts listening. Starting a running broker succeeds immediately.</summary>
    /// <returns>Success, or <see cref="MqttErrorCodes.BrokerStartFailed"/> (port in use, address not available, bad certificate).</returns>
    public async Task<Result> StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning)
                return Result.Success();

            MqttServer server;
            X509Certificate2? certificate = null;
            try
            {
                server = new MqttServerFactory().CreateMqttServer(BuildOptions(Config, out certificate));
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
            {
                certificate?.Dispose();
                _logger.Error($"MQTT broker: cannot load the TLS certificate: {ex.Message}");
                return MqttErrors.BrokerStartFailed(Config.Tls.Port, ex);
            }

            server.ValidatingConnectionAsync += OnValidatingConnectionAsync;
            server.ClientConnectedAsync += OnClientConnectedAsync;
            server.ClientDisconnectedAsync += OnClientDisconnectedAsync;
            server.SessionDeletedAsync += OnSessionDeletedAsync;
            server.InterceptingPublishAsync += OnInterceptingPublishAsync;
            server.InterceptingSubscriptionAsync += OnInterceptingSubscriptionAsync;
            server.RetainedMessageChangedAsync += OnRetainedChangedAsync;
            server.RetainedMessagesClearedAsync += OnRetainedClearedAsync;

            if (_retainedFile is not null && Config.PersistRetainedMessages)
            {
                var store = new RetainedMessageStore(_retainedFile, _logger);
                var loaded = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
                TrackRetained(loaded.Select(m => m.Topic));
                server.LoadingRetainedMessageAsync += e => { e.LoadedRetainedMessages = loaded; return Task.CompletedTask; };
                _retained = store;
            }

            try
            {
                await server.StartAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException)
            {
                server.Dispose();
                certificate?.Dispose();
                await DisposeRetainedAsync().ConfigureAwait(false);
                _logger.Error($"MQTT broker: could not listen on {Endpoint}: {ex.Message}");
                return MqttErrors.BrokerStartFailed(Config.Port, ex);
            }

            _server = server;
            _certificate = certificate;
            _sweepCancel = new CancellationTokenSource();
            _sweeper = Task.Run(() => SweepLoopAsync(_sweepCancel.Token), CancellationToken.None);
            _logger.Info($"MQTT broker listening on {Endpoint} (anonymous {(Config.AllowAnonymous ? "allowed" : "refused")}, {Config.Users.Count} user(s))");
            if (!Config.IsLoopback && !Config.Tls.Enabled)
                _logger.Warning($"MQTT broker: {Endpoint} is reachable from the network without TLS; passwords and payloads travel in clear. Configure broker.tls.");
            _events?.Publish(new MqttBrokerStartedEvent(Endpoint));
            return Result.Success();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Disconnects every client, stops listening and saves the retained messages.</summary>
    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var server = _server;
            _server = null;
            if (server is null)
                return;
            await StopSweeperAsync().ConfigureAwait(false);
            try
            {
                await server.StopAsync(new MqttServerStopOptionsBuilder().Build()).ConfigureAwait(false);
            }
            finally
            {
                server.Dispose();
                _certificate?.Dispose();
                _certificate = null;
                _sessions.Clear();
                lock (_lock)
                    _retainedTopics.Clear();
                await DisposeRetainedAsync().ConfigureAwait(false);
            }
            _logger.Info("MQTT broker stopped");
            _events?.Publish(new MqttBrokerStoppedEvent());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Applies new settings. Listener changes (ports, address, TLS, storage) restart the broker, which disconnects
    /// every client (they reconnect on their own). User and limit changes alone apply without a restart.
    /// </summary>
    public async Task<Result> ReconfigureAsync(MqttBrokerConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        var previous = Config;
        Config = config;
        if (_authorizer is ConfigBrokerAuthorizer configAuthorizer)
            configAuthorizer.Update(config);

        if (previous.ListenerEquals(config) || !IsRunning)
            return Result.Success();

        await StopAsync().ConfigureAwait(false);
        return await StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops the broker.</summary>
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    // ── Operations ────────────────────────────────────────────────────────────

    /// <summary>The clients connected right now.</summary>
    /// <returns>The clients, or <see cref="MqttErrorCodes.BrokerNotRunning"/>.</returns>
    public async Task<Result<IReadOnlyList<MqttBrokerClientInfo>>> GetClientsAsync(CancellationToken cancellationToken = default)
    {
        var server = _server;
        if (server is null)
            return MqttErrors.BrokerNotRunning();
        cancellationToken.ThrowIfCancellationRequested();
        var clients = await server.GetClientsAsync().ConfigureAwait(false);
        return Result<IReadOnlyList<MqttBrokerClientInfo>>.Success([.. clients.Select(c => new MqttBrokerClientInfo(
            c.Id,
            c.RemoteEndPoint?.ToString() ?? "",
            new DateTimeOffset(DateTime.SpecifyKind(c.ConnectedTimestamp, DateTimeKind.Utc)),
            new DateTimeOffset(DateTime.SpecifyKind(c.LastPacketReceivedTimestamp, DateTimeKind.Utc)),
            c.ReceivedApplicationMessagesCount,
            c.SentApplicationMessagesCount))]);
    }

    /// <summary>
    /// Publishes a message as the broker itself (client id <see cref="BrokerClientId"/>): it reaches every
    /// subscribed client and, when retained, is stored. Taps do not see it; they only see what clients publish.
    /// </summary>
    /// <returns>Success, or <see cref="MqttErrorCodes.InvalidTopic"/>, <see cref="MqttErrorCodes.BrokerNotRunning"/>.</returns>
    public async Task<Result> PublishAsync(string topic, ReadOnlyMemory<byte> payload, MqttPublishOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (MqttTopic.ValidateTopic(topic) is { } invalid)
            return MqttErrors.InvalidTopic(invalid);
        var server = _server;
        if (server is null)
            return MqttErrors.BrokerNotRunning();

        var message = MqttMessageMapper.ToNative(topic, payload, options ?? MqttPublishOptions.Default);
        try
        {
            var injected = new InjectedMqttApplicationMessage(message) { SenderClientId = BrokerClientId, CustomSessionItems = _brokerSessionItems };
            await server.InjectApplicationMessage(injected, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return MqttErrors.Cancelled();
        }
    }

    /// <summary>Publishes UTF-8 text as the broker. See <see cref="PublishAsync(string, ReadOnlyMemory{byte}, MqttPublishOptions?, CancellationToken)"/>.</summary>
    public Task<Result> PublishAsync(string topic, string payload, MqttPublishOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return PublishAsync(topic, Encoding.UTF8.GetBytes(payload), options, cancellationToken);
    }

    /// <summary>
    /// Calls <paramref name="handler"/> for every message a client publishes to a topic matching
    /// <paramref name="filter"/> (after the authorizer allowed it). <see cref="MqttMessage.SenderClientId"/> and
    /// <see cref="MqttMessage.SenderUserName"/> say who sent it. Taps run on the sending client's connection:
    /// keep them short or hand work off. Dispose the result to stop.
    /// </summary>
    /// <returns>The tap, or <see cref="MqttErrorCodes.InvalidFilter"/>.</returns>
    public Result<IDisposable> Tap(string filter, Func<MqttMessage, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (MqttTopic.ValidateFilter(filter) is { } invalid)
            return MqttErrors.InvalidFilter(invalid);
        var tap = new BrokerTap(this, filter, handler);
        lock (_lock)
            _taps.Add(tap);
        return tap;
    }


    // ── Internals ─────────────────────────────────────────────────────────────

    private static MqttServerOptions BuildOptions(MqttBrokerConfig config, out X509Certificate2? certificate)
    {
        certificate = null;
        var address = IPAddress.Parse(config.BindAddress);
        var builder = new MqttServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointPort(config.Port)
            .WithMaxPendingMessagesPerClient(config.MaxPendingMessagesPerClient)
            .WithPendingMessagesOverflowStrategy(MqttPendingMessagesOverflowStrategy.DropOldestQueuedMessage)
            .WithDefaultCommunicationTimeout(ConnectTimeout);

        // Listen on exactly the configured address. "0.0.0.0" also opens IPv6 ("::"), so the whole network is reachable
        // either way; a specific IPv4 address must not leave an IPv6 listener open behind it, and vice versa.
        var (v4, v6) = address.AddressFamily == AddressFamily.InterNetworkV6 ? (IPAddress.None, address)
            : address.Equals(IPAddress.Any) ? (IPAddress.Any, IPAddress.IPv6Any)
            : (address, IPAddress.None);
        builder.WithDefaultEndpointBoundIPAddress(v4).WithDefaultEndpointBoundIPV6Address(v6);

        if (config.Tls.Enabled)
        {
            certificate = config.Tls.LoadCertificate();
            builder.WithEncryptedEndpoint()
                .WithEncryptedEndpointPort(config.Tls.Port)
                .WithEncryptionCertificate(certificate)
                .WithEncryptionSslProtocol(SslProtocols.None)   // the OS default set of versions
                .WithEncryptedEndpointBoundIPAddress(v4)
                .WithEncryptedEndpointBoundIPV6Address(v6);
            if (config.Tls.DisablePlainPort)
                builder.WithoutDefaultEndpoint();
        }

        var options = builder.Build();
        options.EnablePersistentSessions = true;   // honour CleanSession = false from clients
        return options;
    }

    private async Task StopSweeperAsync()
    {
        var cancel = _sweepCancel;
        var sweeper = _sweeper;
        _sweepCancel = null;
        _sweeper = null;
        if (cancel is null || sweeper is null)
            return;
        await cancel.CancelAsync().ConfigureAwait(false);
        try
        {
            await sweeper.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }
        cancel.Dispose();
    }

    private async Task DisposeRetainedAsync()
    {
        if (_retained is not null)
        {
            await _retained.DisposeAsync().ConfigureAwait(false);
            _retained = null;
        }
    }

    private void RemoveTap(BrokerTap tap)
    {
        lock (_lock)
            _taps.Remove(tap);
    }

    /// <summary>Who holds a client id, and since when its client has been away (null while connected).</summary>
    private sealed record SessionOwner(string? Username, DateTimeOffset? DisconnectedAt);

    /// <summary>A registered tap; disposing it removes it.</summary>
    private sealed class BrokerTap(MqttBrokerService owner, string filter, Func<MqttMessage, CancellationToken, Task> handler) : IDisposable
    {
        public string Filter { get; } = filter;

        public Func<MqttMessage, CancellationToken, Task> Handler { get; } = handler;

        public void Dispose() => owner.RemoveTap(this);
    }
}
