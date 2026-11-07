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
                if (authFailed)
                {
                    _wantConnected = false;
                    SetState(MqttConnectionState.Disconnected);
                    break;
                }
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
