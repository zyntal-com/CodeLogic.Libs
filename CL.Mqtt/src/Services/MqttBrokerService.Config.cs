using System.Collections;
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

public sealed partial class MqttBrokerService
{

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
            if (HasNetworkPlainListener(Config))
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
                lock (_sessionLock)
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
}
