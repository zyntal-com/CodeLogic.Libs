using System.Net;
using CL.Mqtt.Events;
using CL.Mqtt.Models;
using MQTTnet.Protocol;
using MQTTnet.Server;

namespace CL.Mqtt.Services;

/// <summary>Server event handlers, the limits they enforce and the session sweep of <see cref="MqttBrokerService"/>.</summary>
public sealed partial class MqttBrokerService
{
    // ── Server events ─────────────────────────────────────────────────────────

    private async Task OnValidatingConnectionAsync(ValidatingConnectionEventArgs e)
    {
        var username = string.IsNullOrEmpty(e.UserName) ? null : e.UserName;
        var endpoint = e.RemoteEndPoint?.ToString() ?? "";
        var request = new MqttBrokerConnectRequest(e.ClientId, username, e.Password, endpoint, e.IsSecureConnection);

        // '$' ids are the broker's own (see BrokerClientId): a client that could claim one would publish as the broker.
        if (e.ClientId.StartsWith('$'))
        {
            Refuse(e, MqttConnectReasonCode.ClientIdentifierNotValid, request, "reserved client id");
            return;
        }

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
            Refuse(e, MqttConnectReasonCode.BadUserNameOrPassword, request, "credentials");
            return;
        }
        if (string.IsNullOrEmpty(e.ClientId))
        {
            Refuse(e, MqttConnectReasonCode.ClientIdentifierNotValid, request, "empty client id");
            return;
        }
        if (!TryReserveSession(e.ClientId, username, endpoint, DateTimeOffset.UtcNow, out var generation, out var reason))
        {
            var quota = reason.StartsWith("session limit", StringComparison.Ordinal);
            var code = quota
                ? e.ProtocolVersion == MQTTnet.Formatter.MqttProtocolVersion.V500
                    ? MqttConnectReasonCode.QuotaExceeded : MqttConnectReasonCode.ServerUnavailable
                : MqttConnectReasonCode.ClientIdentifierNotValid;
            Refuse(e, code, request, reason);
            return;
        }
        e.SessionItems[_sessionKey] = generation;
    }

    private void Refuse(ValidatingConnectionEventArgs e, MqttConnectReasonCode code, MqttBrokerConnectRequest request, string reason)
    {
        e.ReasonCode = code;
        _logger.Warning($"MQTT broker: refused {request} ({CL.Internal.LogText.Sanitize(reason, 256)})");
        _events?.Publish(new MqttBrokerConnectionRejectedEvent(request.ClientId, request.Username, request.Endpoint, reason));
    }

    private Task OnClientConnectedAsync(ClientConnectedEventArgs e)
    {
        var username = string.IsNullOrEmpty(e.UserName) ? null : e.UserName;
        SessionConnected(e.ClientId, e.RemoteEndPoint?.ToString() ?? "", e.SessionItems);
        _logger.Debug($"MQTT broker: client '{CL.Internal.LogText.Sanitize(e.ClientId)}' connected ({CL.Internal.LogText.Sanitize(username ?? "anonymous")}, {e.RemoteEndPoint})");
        _events?.Publish(new MqttBrokerClientConnectedEvent(e.ClientId, username, e.RemoteEndPoint?.ToString() ?? ""));
        return Task.CompletedTask;
    }

    private Task OnClientDisconnectedAsync(ClientDisconnectedEventArgs e)
    {
        var username = string.IsNullOrEmpty(e.UserName) ? null : e.UserName;
        var clean = e.DisconnectType == MqttClientDisconnectType.Clean;
        SessionDisconnected(e.ClientId, e.RemoteEndPoint?.ToString() ?? "", e.SessionItems);
        _logger.Debug($"MQTT broker: client '{CL.Internal.LogText.Sanitize(e.ClientId)}' disconnected ({(clean ? "clean" : e.DisconnectType.ToString())})");
        _events?.Publish(new MqttBrokerClientDisconnectedEvent(e.ClientId, username, clean));
        return Task.CompletedTask;
    }

    private Task OnSessionDeletedAsync(SessionDeletedEventArgs e)
    {
        SessionDeleted(e.Id, e.SessionItems);
        return Task.CompletedTask;
    }

    private async Task OnInterceptingPublishAsync(InterceptingPublishEventArgs e)
    {
        // The broker's own publishes (PublishAsync) pass through here too. They are trusted and are not shown to
        // taps. They are recognised by their session items, never by the client id, which a client could claim.
        if (ReferenceEquals(e.SessionItems, _brokerSessionItems))
            return;

        var topic = e.ApplicationMessage.Topic;
        var username = string.IsNullOrEmpty(e.UserName) ? null : e.UserName;
        if (!Authorizer.CanPublish(username, e.ClientId, topic))
        {
            e.ProcessPublish = false;
            // MQTT 5 publishers (QoS 1/2) see this reason code; MQTT 3.1.1 has no way to report it.
            e.Response.ReasonCode = MqttPubAckReasonCode.NotAuthorized;
            _logger.Debug($"MQTT broker: dropped publish by '{CL.Internal.LogText.Sanitize(e.ClientId)}' to '{CL.Internal.LogText.Sanitize(topic)}' (not allowed)");
            return;
        }

        if (e.ApplicationMessage.Retain && !TryReserveRetained(topic, e.ApplicationMessage.Payload.Length, out var limit))
        {
            e.ProcessPublish = false;
            e.Response.ReasonCode = MqttPubAckReasonCode.QuotaExceeded;
            _logger.Warning($"MQTT broker: dropped retained publish by '{CL.Internal.LogText.Sanitize(e.ClientId)}' to '{CL.Internal.LogText.Sanitize(topic)}' ({limit})");
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
                _logger.Error($"MQTT broker: tap for '{CL.Internal.LogText.Sanitize(tap.Filter)}' failed on '{CL.Internal.LogText.Sanitize(topic)}': {ex}");
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
            _logger.Debug($"MQTT broker: refused subscription of '{CL.Internal.LogText.Sanitize(e.ClientId)}' to '{CL.Internal.LogText.Sanitize(filter)}'");
        }
        return Task.CompletedTask;
    }

    private Task OnRetainedChangedAsync(RetainedMessageChangedEventArgs e)
    {
        TrackRetained(e.StoredRetainedMessages.Select(m => m.Topic));
        _retained?.Schedule(e.StoredRetainedMessages);
        return Task.CompletedTask;
    }

    private Task OnRetainedClearedAsync(EventArgs _)
    {
        TrackRetained([]);
        _retained?.Schedule([]);
        return Task.CompletedTask;
    }

    // ── Limits ────────────────────────────────────────────────────────────────

    private void TrackRetained(IEnumerable<string> topics)
    {
        var set = new HashSet<string>(topics, StringComparer.Ordinal);
        lock (_lock)
            _retainedTopics = set;
    }

    /// <summary>Checks a retained publish against the retained limits; a clear (empty payload) always passes.</summary>
    private bool TryReserveRetained(string topic, long payloadLength, out string limit)
    {
        limit = "";
        if (payloadLength == 0)
            return true;
        if (payloadLength > Config.MaxRetainedPayloadBytes)
        {
            limit = $"payload {payloadLength} bytes exceeds maxRetainedPayloadBytes {Config.MaxRetainedPayloadBytes}";
            return false;
        }
        lock (_lock)
        {
            if (_retainedTopics.Contains(topic))
                return true;
            if (_retainedTopics.Count >= Config.MaxRetainedMessages)
            {
                limit = $"maxRetainedMessages {Config.MaxRetainedMessages} reached";
                return false;
            }
            _retainedTopics.Add(topic);   // reserve now; the change event confirms it
            return true;
        }
    }

}
