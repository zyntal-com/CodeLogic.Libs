using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CL.Mqtt.Events;
using CL.Mqtt.Models;
using CodeLogic.Core.Events;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using MQTTnet;
using MQTTnet.Adapter;
using MQTTnet.Exceptions;
using MQTTnet.Formatter;

namespace CL.Mqtt.Services;

/// <summary>
/// One client connection to an MQTT broker.
/// <para>
/// <b>Connection.</b> <see cref="ConnectAsync"/> connects once and returns the outcome. With
/// <see cref="MqttConnectionConfig.AutoReconnect"/> (the default) a failed first attempt or a lost connection
/// is retried in the background with exponential back-off and jitter, until <see cref="DisconnectAsync"/>.
/// </para>
/// <para>
/// <b>Subscriptions</b> are kept by the client, not only by the broker: a handler registered with
/// <see cref="SubscribeAsync(string, Func{MqttMessage, CancellationToken, Task}, MqttQos, CancellationToken)"/> keeps
/// working across reconnects, and may be registered before the first connect. Several handlers can share a filter;
/// the broker sees one subscription per filter.
/// </para>
/// <para>
/// <b>Handlers</b> run one after another on the client's receive loop, in the order messages arrive. Keep them
/// short or hand work off; a slow handler delays every later message. An exception in a handler is logged and
/// does not affect other handlers.
/// </para>
/// </summary>
public sealed class MqttClientService : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly IEventBus? _events;
    private readonly TimeProvider _time;
    private readonly IMqttClient _client;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<MqttSubscription> _subscriptions = [];

    private volatile bool _wantConnected;
    private volatile bool _disposed;
    private bool _everConnected;
    private Task? _reconnectLoop;
    private CancellationTokenSource? _reconnectCancel;
    private MqttConnectionConfig _config;

    /// <summary>Creates a client. It does not connect until <see cref="ConnectAsync"/> is called.</summary>
    /// <param name="name">Connection name, used in logs, events and errors.</param>
    /// <param name="config">Connection settings; validate them first (<see cref="MqttConnectionConfig.Validate"/>).</param>
    /// <param name="logger">Logger for connection changes and handler failures.</param>
    /// <param name="events">Optional event bus for <see cref="MqttConnectedEvent"/> and <see cref="MqttDisconnectedEvent"/>.</param>
    /// <param name="time">Clock for reconnect delays; <see cref="TimeProvider.System"/> when null.</param>
    public MqttClientService(string name, MqttConnectionConfig config, ILogger logger, IEventBus? events = null, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        Name = name;
        _config = config;
        _logger = logger;
        _events = events;
        _time = time ?? TimeProvider.System;
        ClientId = ResolveClientId(name, config);

        _client = new MqttClientFactory().CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
        _client.DisconnectedAsync += OnDisconnectedAsync;
    }

    /// <summary>The connection name.</summary>
    public string Name { get; }

    /// <summary>The client id sent to the broker.</summary>
    public string ClientId { get; private set; }

    /// <summary>The settings in use.</summary>
    public MqttConnectionConfig Config => _config;

    /// <summary>Where the client is in its connection lifecycle.</summary>
    public MqttConnectionState State { get; private set; } = MqttConnectionState.Disconnected;

    /// <summary>True while connected to the broker.</summary>
    public bool IsConnected => _client.IsConnected;

    /// <summary>When the current connection was made (UTC); null while not connected.</summary>
    public DateTimeOffset? ConnectedSince { get; private set; }

    /// <summary>Failed attempts since the connection was last up.</summary>
    public int FailedAttempts { get; private set; }

    /// <summary>The last connection error, or null after a successful connect.</summary>
    public string? LastError { get; private set; }

    /// <summary>Number of registered handlers.</summary>
    public int SubscriptionCount
    {
        get { lock (_lock) return _subscriptions.Count; }
    }

    /// <summary>
    /// The underlying MQTTnet client, for features this wrapper does not cover (enhanced authentication,
    /// packet inspection). Do not connect, disconnect or subscribe through it: the wrapper would not know.
    /// </summary>
    public IMqttClient RawClient => _client;

    // ── Connection ────────────────────────────────────────────────────────────

    /// <summary>
    /// Connects to the broker. When the attempt fails and <see cref="MqttConnectionConfig.AutoReconnect"/> is on,
    /// the client keeps retrying in the background (the returned error still describes the first failure).
    /// Calling it while connected succeeds immediately.
    /// </summary>
    /// <returns>
    /// Success, or <see cref="MqttErrorCodes.AuthenticationFailed"/>, <see cref="MqttErrorCodes.ConnectFailed"/>,
    /// <see cref="MqttErrorCodes.Timeout"/>, <see cref="MqttErrorCodes.Cancelled"/>, <see cref="MqttErrorCodes.Disposed"/>.
    /// </returns>
    public async Task<Result> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return MqttErrors.Disposed($"MQTT connection '{Name}'");
        _wantConnected = true;

        var result = await TryConnectOnceAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsFailure && _config.AutoReconnect && !cancellationToken.IsCancellationRequested)
            StartReconnectLoop();
        return result;
    }

    /// <summary>Disconnects cleanly and stops reconnecting. Subscriptions are kept for the next <see cref="ConnectAsync"/>.</summary>
    public async Task<Result> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _wantConnected = false;
        await StopReconnectLoopAsync().ConfigureAwait(false);

        try
        {
            if (_client.IsConnected)
            {
                var options = new MqttClientDisconnectOptionsBuilder().WithReason(MqttClientDisconnectOptionsReason.NormalDisconnection).Build();
                await _client.DisconnectAsync(options, cancellationToken).ConfigureAwait(false);
            }
            SetState(MqttConnectionState.Disconnected);
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return MqttErrors.Cancelled();
        }
        catch (Exception ex) when (ex is MqttCommunicationException or ObjectDisposedException)
        {
            // The connection was already going away; the result is the same.
            SetState(MqttConnectionState.Disconnected);
            return Result.Success();
        }
    }

    /// <summary>
    /// Switches to new settings: disconnects, applies <paramref name="config"/> and reconnects when the client was
    /// connected (or trying to). Subscriptions are kept, so code holding this client keeps working.
    /// </summary>
    public async Task<Result> ReconfigureAsync(MqttConnectionConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        var wasWanted = _wantConnected;
        await DisconnectAsync(cancellationToken).ConfigureAwait(false);
        _config = config;
        ClientId = ResolveClientId(Name, config);
        return wasWanted ? await ConnectAsync(cancellationToken).ConfigureAwait(false) : Result.Success();
    }

    // ── Publish ───────────────────────────────────────────────────────────────

    /// <summary>Publishes raw bytes.</summary>
    /// <param name="topic">The topic name (no wildcards).</param>
    /// <param name="payload">The payload; empty is allowed (and clears a retained message when retained).</param>
    /// <param name="options">QoS, retain and MQTT 5 properties; <see cref="MqttPublishOptions.Default"/> when null.</param>
    /// <param name="cancellationToken">Cancels the publish.</param>
    /// <returns>
    /// Success once the broker acknowledged (QoS 1/2) or the message was sent (QoS 0); otherwise
    /// <see cref="MqttErrorCodes.InvalidTopic"/>, <see cref="MqttErrorCodes.NotConnected"/>,
    /// <see cref="MqttErrorCodes.PublishRejected"/>, <see cref="MqttErrorCodes.Timeout"/>, <see cref="MqttErrorCodes.Cancelled"/>.
    /// Publishing never queues while disconnected: the caller decides whether to retry.
    /// </returns>
    public async Task<Result> PublishAsync(string topic, ReadOnlyMemory<byte> payload, MqttPublishOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (MqttTopic.ValidateTopic(topic) is { } invalid)
            return MqttErrors.InvalidTopic(invalid);
        if (_disposed)
            return MqttErrors.Disposed($"MQTT connection '{Name}'");
        if (!_client.IsConnected)
            return MqttErrors.NotConnected(Name);

        var message = MqttMessageMapper.ToNative(topic, payload, options ?? MqttPublishOptions.Default);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(_config.TimeoutSeconds));
        try
        {
            var result = await _client.PublishAsync(message, timeout.Token).ConfigureAwait(false);
            return result.IsSuccess
                ? Result.Success()
                : MqttErrors.PublishRejected(topic, result.ReasonCode.ToString(), result.ReasonString);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return MqttErrors.Cancelled();
        }
        catch (OperationCanceledException)
        {
            return _disposed ? MqttErrors.Disposed($"MQTT connection '{Name}'") : MqttErrors.Timeout(Name, $"publish to '{topic}'");
        }
        catch (MqttCommunicationTimedOutException)
        {
            return MqttErrors.Timeout(Name, $"publish to '{topic}'");
        }
        catch (Exception ex) when (ex is MqttCommunicationException or MqttClientNotConnectedException or ObjectDisposedException)
        {
            return MqttErrors.NotConnected(Name);
        }
    }

    /// <summary>Publishes UTF-8 text. See <see cref="PublishAsync(string, ReadOnlyMemory{byte}, MqttPublishOptions?, CancellationToken)"/>.</summary>
    public Task<Result> PublishAsync(string topic, string payload, MqttPublishOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return PublishAsync(topic, Encoding.UTF8.GetBytes(payload), options, cancellationToken);
    }

    /// <summary>
    /// Publishes <paramref name="value"/> as JSON with reflection-based serialization. The content type is set to
    /// <c>application/json</c> unless <paramref name="options"/> sets one.
    /// </summary>
    public Task<Result> PublishJsonAsync<T>(string topic, T value, JsonSerializerOptions? json = null, MqttPublishOptions? options = null, CancellationToken cancellationToken = default)
    {
        byte[] bytes;
        try
        {
            bytes = JsonSerializer.SerializeToUtf8Bytes(value, json);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return Task.FromResult<Result>(MqttErrors.InvalidJson($"The value for '{topic}' cannot be written as JSON.", ex));
        }
        return PublishAsync(topic, bytes, WithJsonContentType(options), cancellationToken);
    }

    /// <summary>Publishes <paramref name="value"/> as JSON with source-generated metadata (trimming and Native AOT safe).</summary>
    public Task<Result> PublishJsonAsync<T>(string topic, T value, JsonTypeInfo<T> typeInfo, MqttPublishOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        byte[] bytes;
        try
        {
            bytes = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return Task.FromResult<Result>(MqttErrors.InvalidJson($"The value for '{topic}' cannot be written as JSON.", ex));
        }
        return PublishAsync(topic, bytes, WithJsonContentType(options), cancellationToken);
    }

    // ── Subscribe ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers <paramref name="handler"/> for every message matching <paramref name="filter"/>.
    /// While connected, the broker is asked first and a refusal is returned as an error. While not connected the
    /// handler is registered and the broker is asked on the next connect.
    /// </summary>
    /// <param name="filter">Topic filter; <c>+</c> and <c>#</c> wildcards are allowed.</param>
    /// <param name="handler">Called for each matching message. The token is cancelled when the client is disposed.</param>
    /// <param name="qos">Highest QoS the broker should deliver with.</param>
    /// <param name="cancellationToken">Cancels the subscribe request.</param>
    /// <returns>The subscription (dispose it to stop), or <see cref="MqttErrorCodes.InvalidFilter"/>,
    /// <see cref="MqttErrorCodes.SubscribeRejected"/>, <see cref="MqttErrorCodes.Timeout"/>, <see cref="MqttErrorCodes.Cancelled"/>.</returns>
    public async Task<Result<MqttSubscription>> SubscribeAsync(
        string filter,
        Func<MqttMessage, CancellationToken, Task> handler,
        MqttQos qos = MqttQos.AtLeastOnce,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (MqttTopic.ValidateFilter(filter) is { } invalid)
            return MqttErrors.InvalidFilter(invalid);
        if (_disposed)
            return MqttErrors.Disposed($"MQTT connection '{Name}'");

        var subscription = new MqttSubscription(this, filter, qos, handler);
        bool askBroker;
        lock (_lock)
        {
            // Ask the broker only when this filter is new, or when this handler needs a higher QoS than the others.
            var existing = _subscriptions.Where(s => s.Filter == filter).Select(s => (int)s.Qos).DefaultIfEmpty(-1).Max();
            askBroker = (int)qos > existing;
            _subscriptions.Add(subscription);
        }

        if (askBroker && _client.IsConnected)
        {
            var sent = await SendSubscribeAsync([(filter, qos)], cancellationToken).ConfigureAwait(false);
            if (sent.IsFailure)
            {
                lock (_lock)
                    _subscriptions.Remove(subscription);
                return sent.Error!;
            }
        }
        _logger.Debug($"MQTT '{Name}': subscribed to '{filter}' (QoS {(int)qos})");
        return subscription;
    }

    /// <summary>Registers a synchronous handler. See <see cref="SubscribeAsync(string, Func{MqttMessage, CancellationToken, Task}, MqttQos, CancellationToken)"/>.</summary>
    public Task<Result<MqttSubscription>> SubscribeAsync(string filter, Action<MqttMessage> handler, MqttQos qos = MqttQos.AtLeastOnce, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return SubscribeAsync(filter, (message, _) => { handler(message); return Task.CompletedTask; }, qos, cancellationToken);
    }

    /// <summary>Removes a subscription; unsubscribes from the broker when no other handler uses its filter.</summary>
    internal async Task RemoveAsync(MqttSubscription subscription)
    {
        bool lastForFilter;
        lock (_lock)
        {
            _subscriptions.Remove(subscription);
            lastForFilter = !_subscriptions.Any(s => s.Filter == subscription.Filter);
        }
        if (!lastForFilter || !_client.IsConnected || _disposed)
            return;

        try
        {
            var options = new MqttClientUnsubscribeOptionsBuilder().WithTopicFilter(subscription.Filter).Build();
            await _client.UnsubscribeAsync(options, _lifetime.Token).ConfigureAwait(false);
            _logger.Debug($"MQTT '{Name}': unsubscribed from '{subscription.Filter}'");
        }
        catch (Exception ex) when (ex is MqttCommunicationException or OperationCanceledException or MqttClientNotConnectedException or ObjectDisposedException)
        {
            // Not connected any more: the broker forgets the subscription with the session anyway.
        }
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    /// <summary>Disconnects, stops reconnecting and releases the client. Handlers are cancelled.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        _disposed = true;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _client.ApplicationMessageReceivedAsync -= OnMessageReceivedAsync;
        _client.DisconnectedAsync -= OnDisconnectedAsync;
        _client.Dispose();
        _connectGate.Dispose();
        _lifetime.Dispose();
        lock (_lock)
            _subscriptions.Clear();
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    /// <summary>One connect attempt, serialized so the reconnect loop and callers never connect twice at once.</summary>
    private async Task<Result> TryConnectOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return MqttErrors.Cancelled();
        }
        catch (ObjectDisposedException)
        {
            return MqttErrors.Disposed($"MQTT connection '{Name}'");
        }

        try
        {
            if (_client.IsConnected)
                return Result.Success();
            if (!_wantConnected || _disposed)
                return MqttErrors.Cancelled();

            SetState(FailedAttempts > 0 ? MqttConnectionState.Reconnecting : MqttConnectionState.Connecting);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(_config.TimeoutSeconds));

            var connect = await _client.ConnectAsync(BuildOptions(), timeout.Token).ConfigureAwait(false);
            if (connect.ResultCode != MqttClientConnectResultCode.Success)
                return Failed(MapConnectResult(connect.ResultCode, connect.ReasonString));

            // Subscriptions belong to the client: ask the broker again on every connect.
            var resubscribed = await ResubscribeAsync(cancellationToken).ConfigureAwait(false);
            if (resubscribed.IsFailure)
                _logger.Warning($"MQTT '{Name}': connected, but re-subscribing failed: {resubscribed.Error!.Message}");

            var isReconnect = _everConnected;
            _everConnected = true;
            FailedAttempts = 0;
            LastError = null;
            ConnectedSince = _time.GetUtcNow();
            SetState(MqttConnectionState.Connected);
            _logger.Info($"MQTT '{Name}': connected to {_config.Host}:{_config.Port} as '{ClientId}'");
            _events?.Publish(new MqttConnectedEvent(Name, ClientId, isReconnect));
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failed(MqttErrors.Cancelled());
        }
        catch (OperationCanceledException)
        {
            return Failed(_disposed ? MqttErrors.Disposed($"MQTT connection '{Name}'") : MqttErrors.Timeout(Name, "connect"));
        }
        catch (MqttCommunicationTimedOutException)
        {
            return Failed(MqttErrors.Timeout(Name, "connect"));
        }
        catch (MqttConnectingFailedException ex)
        {
            // Thrown when the broker answers CONNACK with a failure code.
            return Failed(ex.Message.Contains("BadUserNameOrPassword", StringComparison.Ordinal) || ex.Message.Contains("NotAuthorized", StringComparison.Ordinal)
                ? MqttErrors.AuthenticationFailed(Name, ex.Message)
                : MqttErrors.ConnectFailed(Name, ex.Message));
        }
        catch (MqttCommunicationException ex)
        {
            return Failed(MqttErrors.ConnectFailed(Name, ex.InnerException?.Message ?? ex.Message));
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or System.Security.Authentication.AuthenticationException)
        {
            return Failed(MqttErrors.ConnectFailed(Name, ex.Message));
        }
        finally
        {
            try
            {
                _connectGate.Release();
            }
            catch (ObjectDisposedException)
            {
                // DisposeAsync released the gate while this attempt was finishing.
            }
        }
    }

    /// <summary>Records a failed attempt and returns its error.</summary>
    private Error Failed(Error error)
    {
        FailedAttempts++;
        LastError = error.Message;
        SetState(_wantConnected && _config.AutoReconnect && !_disposed ? MqttConnectionState.Reconnecting : MqttConnectionState.Disconnected);
        return error;
    }

    private Error MapConnectResult(MqttClientConnectResultCode code, string? reason) => code switch
    {
        MqttClientConnectResultCode.BadUserNameOrPassword or MqttClientConnectResultCode.NotAuthorized =>
            MqttErrors.AuthenticationFailed(Name, code.ToString()),
        _ => MqttErrors.ConnectFailed(Name, string.IsNullOrEmpty(reason) ? code.ToString() : reason, code.ToString())
    };

    private async Task<Result> ResubscribeAsync(CancellationToken cancellationToken)
    {
        List<(string Filter, MqttQos Qos)> filters;
        lock (_lock)
        {
            filters = [.. _subscriptions.GroupBy(s => s.Filter).Select(g => (g.Key, g.Max(s => s.Qos)))];
        }
        return filters.Count == 0 ? Result.Success() : await SendSubscribeAsync(filters, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result> SendSubscribeAsync(IReadOnlyList<(string Filter, MqttQos Qos)> filters, CancellationToken cancellationToken)
    {
        var builder = new MqttClientSubscribeOptionsBuilder();
        foreach (var (filter, qos) in filters)
            builder.WithTopicFilter(f => f.WithTopic(filter).WithQualityOfServiceLevel(MqttMessageMapper.ToNative(qos)));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(_config.TimeoutSeconds));
        try
        {
            var result = await _client.SubscribeAsync(builder.Build(), timeout.Token).ConfigureAwait(false);
            // Granted codes are 0, 1 and 2 (the QoS granted); 0x80 and above are refusals.
            var refused = result.Items.FirstOrDefault(i => (int)i.ResultCode >= 0x80);
            return refused is null
                ? Result.Success()
                : MqttErrors.SubscribeRejected(refused.TopicFilter.Topic, refused.ResultCode.ToString());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return MqttErrors.Cancelled();
        }
        catch (OperationCanceledException)
        {
            return MqttErrors.Timeout(Name, "subscribe");
        }
        catch (Exception ex) when (ex is MqttCommunicationException or MqttClientNotConnectedException or ObjectDisposedException)
        {
            return MqttErrors.NotConnected(Name);
        }
    }

    private MqttClientOptions BuildOptions()
    {
        var builder = new MqttClientOptionsBuilder()
            .WithClientId(ClientId)
            .WithTcpServer(_config.Host, _config.Port)
            .WithProtocolVersion(_config.Protocol == MqttProtocol.V500 ? MqttProtocolVersion.V500 : MqttProtocolVersion.V311)
            .WithCleanSession(_config.CleanSession)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(_config.KeepAliveSeconds))
            .WithTimeout(TimeSpan.FromSeconds(_config.TimeoutSeconds));

        if (!string.IsNullOrEmpty(_config.Username))
            builder.WithCredentials(_config.Username, _config.Password ?? "");

        if (_config.UseTls)
        {
            var allowUntrusted = _config.AllowUntrustedCertificates;
            builder.WithTlsOptions(tls =>
            {
                tls.UseTls(true).WithTargetHost(_config.Host);
                if (allowUntrusted)
                    tls.WithCertificateValidationHandler(_ => true);
            });
        }

        if (!string.IsNullOrEmpty(_config.WillTopic))
        {
            builder.WithWillTopic(_config.WillTopic)
                .WithWillPayload(_config.WillPayload ?? "")
                .WithWillRetain(_config.WillRetain)
                .WithWillQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce);
        }
        return builder.Build();
    }

    private async Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        MqttSubscription[] matching;
        var topic = e.ApplicationMessage.Topic;
        lock (_lock)
        {
            matching = [.. _subscriptions.Where(s => MqttTopic.Matches(topic, s.Filter))];
        }
        if (matching.Length == 0)
            return;

        var message = MqttMessageMapper.FromNative(e.ApplicationMessage);
        foreach (var subscription in matching)
        {
            if (!subscription.IsActive)
                continue;
            try
            {
                await subscription.Handler(message, _lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.Error($"MQTT '{Name}': handler for '{subscription.Filter}' failed on '{topic}': {ex}");
            }
        }
    }

    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs e)
    {
        if (e.ClientWasConnected)
        {
            ConnectedSince = null;
            var reason = e.Exception?.Message ?? (string.IsNullOrEmpty(e.ReasonString) ? e.Reason.ToString() : e.ReasonString);
            var willReconnect = _wantConnected && _config.AutoReconnect && !_disposed;
            if (_wantConnected)
            {
                LastError = reason;
                _logger.Warning($"MQTT '{Name}': connection lost ({reason}){(willReconnect ? ", reconnecting" : "")}");
                _events?.Publish(new MqttDisconnectedEvent(Name, reason, willReconnect));
            }
            else
            {
                _logger.Info($"MQTT '{Name}': disconnected");
                _events?.Publish(new MqttDisconnectedEvent(Name, "Disconnected", false));
            }
            SetState(willReconnect ? MqttConnectionState.Reconnecting : MqttConnectionState.Disconnected);
            if (willReconnect)
                StartReconnectLoop();
        }
        return Task.CompletedTask;
    }

    /// <summary>Starts the background reconnect loop unless one is already running.</summary>
    private void StartReconnectLoop()
    {
        lock (_lock)
        {
            if (_reconnectLoop is not null || _disposed || !_wantConnected)
                return;
            _reconnectCancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var token = _reconnectCancel.Token;
            _reconnectLoop = Task.Run(() => ReconnectLoopAsync(token), CancellationToken.None);
        }
    }

    private async Task ReconnectLoopAsync(CancellationToken token)
    {
        try
        {
            var attempt = 0;
            while (!token.IsCancellationRequested && _wantConnected && !_client.IsConnected)
            {
                await Task.Delay(ReconnectDelay(attempt++), _time, token).ConfigureAwait(false);
                var result = await TryConnectOnceAsync(token).ConfigureAwait(false);
                // Log the first failure and then every tenth, so a long outage does not flood the log.
                if (result.IsFailure && !token.IsCancellationRequested && (attempt == 1 || attempt % 10 == 0))
                    _logger.Warning($"MQTT '{Name}': reconnect attempt {attempt} failed: {result.Error!.Message}");
            }
        }
        catch (OperationCanceledException)
        {
            // Disconnect or dispose stopped the loop.
        }
        finally
        {
            bool restart;
            lock (_lock)
            {
                _reconnectLoop = null;
                _reconnectCancel?.Dispose();
                _reconnectCancel = null;
                // The connection may have dropped again between the loop's last check and here.
                restart = _wantConnected && !_disposed && !_client.IsConnected && !token.IsCancellationRequested;
            }
            if (restart)
                StartReconnectLoop();
        }
    }

    private async Task StopReconnectLoopAsync()
    {
        Task? loop;
        lock (_lock)
        {
            loop = _reconnectLoop;
            _reconnectCancel?.Cancel();
        }
        if (loop is not null)
            await loop.ConfigureAwait(false);
    }

    /// <summary>Exponential back-off from the minimum to the maximum delay, with ±20 % jitter so many clients do not retry in step.</summary>
    internal TimeSpan ReconnectDelay(int attempt)
    {
        var min = _config.ReconnectMinDelaySeconds;
        var max = _config.ReconnectMaxDelaySeconds;
        var seconds = Math.Min(max, min * Math.Pow(2, Math.Min(attempt, 30)));
        var jitter = 1 + (Random.Shared.NextDouble() * 0.4 - 0.2);
        return TimeSpan.FromSeconds(Math.Clamp(seconds * jitter, min * 0.8, max * 1.2));
    }

    private void SetState(MqttConnectionState state) => State = state;

    private static MqttPublishOptions WithJsonContentType(MqttPublishOptions? options) =>
        options is { ContentType: not null } ? options : (options ?? MqttPublishOptions.Default) with { ContentType = "application/json" };

    private static string ResolveClientId(string name, MqttConnectionConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.ClientId))
            return config.ClientId.Trim();
        var raw = $"cl-{Environment.MachineName}-{name}".ToLowerInvariant();
        return new string([.. raw.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-')]);
    }
}
