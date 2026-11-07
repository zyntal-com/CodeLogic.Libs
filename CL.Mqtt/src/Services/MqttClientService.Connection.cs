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

public sealed partial class MqttClientService
{

    /// <summary>The connection name.</summary>
    public string Name { get; }

    /// <summary>The client id sent to the broker.</summary>
    public string ClientId { get; private set; }

    /// <summary>The settings in use.</summary>
    public MqttConnectionConfig Config => _config;

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
        if (result.IsFailure && result.Error!.Code == MqttErrorCodes.AuthenticationFailed)
        {
            _wantConnected = false;
            SetState(MqttConnectionState.Disconnected);
        }
        else if (result.IsFailure && _config.AutoReconnect && !cancellationToken.IsCancellationRequested)
            StartReconnectLoop(false);
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
}
