using System.Net;
using System.Net.Sockets;
using System.Text;
using CL.Mqtt.Events;
using CL.Mqtt.Models;
using CodeLogic.Core.Events;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using MQTTnet.Protocol;
using MQTTnet.Server;

namespace CL.Mqtt.Services;

/// <summary>
/// The embedded MQTT broker: an MQTT server inside the application process.
/// <para>
/// <b>Security.</b> Every connection, publish and subscription goes through <see cref="Authorizer"/>
/// (by default <see cref="ConfigBrokerAuthorizer"/>, the users in <c>config.mqtt.json</c>). The default bind
/// address is <c>127.0.0.1</c>, so only local programs can connect until you open it up.
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
public sealed class MqttBrokerService : IAsyncDisposable
{
    /// <summary>The client id messages published by the broker itself carry.</summary>
    public const string BrokerClientId = "$broker";

    private readonly ILogger _logger;
    private readonly IEventBus? _events;
    private readonly string? _retainedFile;
    private readonly Lock _lock = new();
    private readonly List<BrokerTap> _taps = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    private MqttServer? _server;
    private RetainedMessageStore? _retained;
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

    /// <summary>The address and port the broker listens on (or will listen on).</summary>
    public string Endpoint => $"{Config.BindAddress}:{Config.Port}";

    /// <summary>The underlying MQTTnet server while running, for features this wrapper does not cover.</summary>
    public MqttServer? RawServer => _server;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>Starts listening. Starting a running broker succeeds immediately.</summary>
    /// <returns>Success, or <see cref="MqttErrorCodes.BrokerStartFailed"/> (port in use, address not available).</returns>
    public async Task<Result> StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning)
                return Result.Success();

            var server = new MqttServerFactory().CreateMqttServer(BuildOptions(Config));
            server.ValidatingConnectionAsync += OnValidatingConnectionAsync;
            server.ClientConnectedAsync += OnClientConnectedAsync;
            server.ClientDisconnectedAsync += OnClientDisconnectedAsync;
            server.InterceptingPublishAsync += OnInterceptingPublishAsync;
            server.InterceptingSubscriptionAsync += OnInterceptingSubscriptionAsync;

            if (_retainedFile is not null && Config.PersistRetainedMessages)
            {
                var store = new RetainedMessageStore(_retainedFile, _logger);
                var loaded = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
                server.LoadingRetainedMessageAsync += e => { e.LoadedRetainedMessages = loaded; return Task.CompletedTask; };
                server.RetainedMessageChangedAsync += e => { store.Schedule(e.StoredRetainedMessages); return Task.CompletedTask; };
                server.RetainedMessagesClearedAsync += _ => { store.Schedule([]); return Task.CompletedTask; };
                _retained = store;
            }

            try
            {
                await server.StartAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException)
            {
                server.Dispose();
                await DisposeRetainedAsync().ConfigureAwait(false);
                _logger.Error($"MQTT broker: could not listen on {Endpoint}: {ex.Message}");
                return MqttErrors.BrokerStartFailed(Config.Port, ex);
            }

            _server = server;
            _logger.Info($"MQTT broker listening on {Endpoint} (anonymous {(Config.AllowAnonymous ? "allowed" : "refused")}, {Config.Users.Count} user(s))");
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
            try
            {
                await server.StopAsync(new MqttServerStopOptionsBuilder().Build()).ConfigureAwait(false);
            }
            finally
            {
                server.Dispose();
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
    /// Applies new settings. Listener changes (port, address, storage) restart the broker, which disconnects
    /// every client (they reconnect on their own). User changes alone apply without a restart.
    /// </summary>
    public async Task<Result> ReconfigureAsync(MqttBrokerConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        var previous = Config;
        Config = config;
        if (_authorizer is ConfigBrokerAuthorizer configAuthorizer)
            configAuthorizer.Update(config);

        var listenerChanged = previous.Port != config.Port
            || !string.Equals(previous.BindAddress, config.BindAddress, StringComparison.Ordinal)
            || previous.PersistRetainedMessages != config.PersistRetainedMessages
            || previous.MaxPendingMessagesPerClient != config.MaxPendingMessagesPerClient;
        if (!listenerChanged || !IsRunning)
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
            await server.InjectApplicationMessage(new InjectedMqttApplicationMessage(message) { SenderClientId = BrokerClientId }, cancellationToken).ConfigureAwait(false);
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

    // ── Server events ─────────────────────────────────────────────────────────

    private async Task OnValidatingConnectionAsync(ValidatingConnectionEventArgs e)
    {
        var username = string.IsNullOrEmpty(e.UserName) ? null : e.UserName;
        var endpoint = e.RemoteEndPoint?.ToString() ?? "";
        var request = new MqttBrokerConnectRequest(e.ClientId, username, e.Password, endpoint, e.IsSecureConnection);

        bool accepted;
        try
        {
            accepted = await Authorizer.AuthenticateAsync(request, e.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Error($"MQTT broker: the authorizer failed for {request}, refusing: {ex}");
            accepted = false;
        }

        if (!accepted)
        {
            e.ReasonCode = MqttConnectReasonCode.BadUserNameOrPassword;
            _logger.Warning($"MQTT broker: refused {request}");
            _events?.Publish(new MqttBrokerConnectionRejectedEvent(e.ClientId, username, endpoint));
        }
    }

    private Task OnClientConnectedAsync(ClientConnectedEventArgs e)
    {
        var username = string.IsNullOrEmpty(e.UserName) ? null : e.UserName;
        _logger.Debug($"MQTT broker: client '{e.ClientId}' connected ({username ?? "anonymous"}, {e.RemoteEndPoint})");
        _events?.Publish(new MqttBrokerClientConnectedEvent(e.ClientId, username, e.RemoteEndPoint?.ToString() ?? ""));
        return Task.CompletedTask;
    }

    private Task OnClientDisconnectedAsync(ClientDisconnectedEventArgs e)
    {
        var username = string.IsNullOrEmpty(e.UserName) ? null : e.UserName;
        var clean = e.DisconnectType == MqttClientDisconnectType.Clean;
        _logger.Debug($"MQTT broker: client '{e.ClientId}' disconnected ({(clean ? "clean" : e.DisconnectType.ToString())})");
        _events?.Publish(new MqttBrokerClientDisconnectedEvent(e.ClientId, username, clean));
        return Task.CompletedTask;
    }

    private async Task OnInterceptingPublishAsync(InterceptingPublishEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var username = string.IsNullOrEmpty(e.UserName) ? null : e.UserName;
        if (!Authorizer.CanPublish(username, e.ClientId, topic))
        {
            e.ProcessPublish = false;
            // MQTT 5 publishers (QoS 1/2) see this reason code; MQTT 3.1.1 has no way to report it.
            e.Response.ReasonCode = MqttPubAckReasonCode.NotAuthorized;
            _logger.Debug($"MQTT broker: dropped publish by '{e.ClientId}' to '{topic}' (not allowed)");
            return;
        }

        BrokerTap[] matching;
        lock (_lock)
            matching = [.. _taps.Where(t => MqttTopic.Matches(topic, t.Filter))];
        if (matching.Length == 0)
            return;

        var message = MqttMessageMapper.FromNative(e.ApplicationMessage, e.ClientId, username);
        foreach (var tap in matching)
        {
            try
            {
                await tap.Handler(message, e.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Error($"MQTT broker: tap for '{tap.Filter}' failed on '{topic}': {ex}");
            }
        }
    }

    private Task OnInterceptingSubscriptionAsync(InterceptingSubscriptionEventArgs e)
    {
        var filter = e.TopicFilter.Topic;
        var username = string.IsNullOrEmpty(e.UserName) ? null : e.UserName;
        if (!Authorizer.CanSubscribe(username, e.ClientId, filter))
        {
            e.ProcessSubscription = false;
            e.Response.ReasonCode = MqttSubscribeReasonCode.NotAuthorized;
            _logger.Debug($"MQTT broker: refused subscription of '{e.ClientId}' to '{filter}'");
        }
        return Task.CompletedTask;
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private static MqttServerOptions BuildOptions(MqttBrokerConfig config)
    {
        var address = IPAddress.Parse(config.BindAddress);
        var builder = new MqttServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointPort(config.Port)
            .WithMaxPendingMessagesPerClient(config.MaxPendingMessagesPerClient);

        // Listen on exactly the configured address. "0.0.0.0" also opens IPv6 ("::"), so the whole network is reachable
        // either way; a specific IPv4 address must not leave an IPv6 listener open behind it, and vice versa.
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            builder.WithDefaultEndpointBoundIPV6Address(address).WithDefaultEndpointBoundIPAddress(IPAddress.None);
        else if (address.Equals(IPAddress.Any))
            builder.WithDefaultEndpointBoundIPAddress(IPAddress.Any).WithDefaultEndpointBoundIPV6Address(IPAddress.IPv6Any);
        else
            builder.WithDefaultEndpointBoundIPAddress(address).WithDefaultEndpointBoundIPV6Address(IPAddress.None);

        var options = builder.Build();
        options.EnablePersistentSessions = true;   // honour CleanSession = false from clients
        return options;
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

    /// <summary>A registered tap; disposing it removes it.</summary>
    private sealed class BrokerTap(MqttBrokerService owner, string filter, Func<MqttMessage, CancellationToken, Task> handler) : IDisposable
    {
        public string Filter { get; } = filter;

        public Func<MqttMessage, CancellationToken, Task> Handler { get; } = handler;

        public void Dispose() => owner.RemoveTap(this);
    }
}
