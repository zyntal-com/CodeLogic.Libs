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
/// Refused credentials are retried too, but only at the longest delay: the broker is not hammered with a bad password.
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
/// This file holds the connection lifecycle; <c>MqttClientService.Messaging.cs</c> holds publish and subscribe.
/// </summary>
public sealed partial class MqttClientService : IAsyncDisposable
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
            StartReconnectLoop(result.Error!.Code == MqttErrorCodes.AuthenticationFailed);
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
            // Thrown when the broker answers CONNACK with a failure code. MQTTnet 5 only puts the code's name in the
            // text ("... failed (BadUserNameOrPassword)."), so every known code is matched by name.
            return Failed(MapConnectResult(ParseConnectResultCode(ex.Message), ex.Message));
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

    /// <summary>Finds the CONNACK reason code named in an MQTTnet connect failure message; <c>UnspecifiedError</c> when none is.</summary>
    internal static MqttClientConnectResultCode ParseConnectResultCode(string message)
    {
        var best = MqttClientConnectResultCode.UnspecifiedError;
        var bestLength = 0;
        foreach (var code in Enum.GetValues<MqttClientConnectResultCode>())
        {
            var name = code.ToString();
            // The longest matching name wins, so "NotAuthorized" is not shadowed by a shorter one.
            if (name.Length > bestLength && message.Contains(name, StringComparison.Ordinal))
                (best, bestLength) = (code, name.Length);
        }
        return best;
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

        // MQTT 5 drops a session at disconnect unless an expiry is sent; without this, cleanSession=false does nothing.
        if (!_config.CleanSession && _config.Protocol == MqttProtocol.V500)
            builder.WithSessionExpiryInterval((uint)Math.Max(1, _config.SessionExpirySeconds));

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
                StartReconnectLoop(authFailed: false);
        }
        return Task.CompletedTask;
    }

    /// <summary>Starts the background reconnect loop unless one is already running.</summary>
    /// <param name="authFailed">True when the last attempt was refused for its credentials: start at the longest delay.</param>
    private void StartReconnectLoop(bool authFailed)
    {
        lock (_lock)
        {
            if (_reconnectLoop is not null || _disposed || !_wantConnected)
                return;
            _reconnectCancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var token = _reconnectCancel.Token;
            _reconnectLoop = Task.Run(() => ReconnectLoopAsync(token, authFailed), CancellationToken.None);
        }
    }

    private async Task ReconnectLoopAsync(CancellationToken token, bool authFailed)
    {
        try
        {
            var attempt = 0;
            while (!token.IsCancellationRequested && _wantConnected && !_client.IsConnected)
            {
                // Bad credentials are not a transient fault: wait the longest delay rather than hammering the broker.
                var delay = authFailed ? MaxReconnectDelay() : ReconnectDelay(attempt);
                attempt++;
                await Task.Delay(delay, _time, token).ConfigureAwait(false);
                var result = await TryConnectOnceAsync(token).ConfigureAwait(false);
                authFailed = result.IsFailure && result.Error!.Code == MqttErrorCodes.AuthenticationFailed;
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
                StartReconnectLoop(authFailed);
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

    /// <summary>The longest back-off, with jitter: used after refused credentials.</summary>
    internal TimeSpan MaxReconnectDelay() => ReconnectDelay(int.MaxValue / 2);

    private void SetState(MqttConnectionState state) => State = state;

    private static string ResolveClientId(string name, MqttConnectionConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.ClientId))
            return config.ClientId.Trim();
        var raw = $"cl-{Environment.MachineName}-{name}".ToLowerInvariant();
        return new string([.. raw.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-')]);
    }
}
