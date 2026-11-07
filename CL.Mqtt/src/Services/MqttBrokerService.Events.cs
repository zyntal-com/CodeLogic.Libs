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
            return;   // MQTTnet assigns an id; there is no session to protect yet.

        // A client id belongs to the user that holds its session: another user may neither take the session over
        // (and inherit its subscriptions and queued messages) nor kick the client off.
        var owner = _sessions.GetValueOrDefault(e.ClientId);
        if (owner is not null && !string.Equals(owner.Username, username, StringComparison.Ordinal))
        {
            Refuse(e, MqttConnectReasonCode.ClientIdentifierNotValid, request, $"client id held by {(owner.Username is null ? "an anonymous client" : $"user '{owner.Username}'")}");
            return;
        }
        var cap = Config.MaxSessionsPerUser;
        if (owner is null && cap > 0 && _sessions.Count(s => string.Equals(s.Value.Username, username, StringComparison.Ordinal)) >= cap)
        {
            // MQTT 3.1.1 has no quota code; "server unavailable" is the one that tells such a client to retry later.
            var code = e.ProtocolVersion == MQTTnet.Formatter.MqttProtocolVersion.V500 ? MqttConnectReasonCode.QuotaExceeded : MqttConnectReasonCode.ServerUnavailable;
            Refuse(e, code, request, $"session limit of {cap} reached");
            return;
        }
        _sessions[e.ClientId] = new SessionOwner(username, null);
    }

    private void Refuse(ValidatingConnectionEventArgs e, MqttConnectReasonCode code, MqttBrokerConnectRequest request, string reason)
    {
        e.ReasonCode = code;
        _logger.Warning($"MQTT broker: refused {request} ({reason})");
        _events?.Publish(new MqttBrokerConnectionRejectedEvent(request.ClientId, request.Username, request.Endpoint, reason));
    }

    private Task OnClientConnectedAsync(ClientConnectedEventArgs e)
    {
        var username = string.IsNullOrEmpty(e.UserName) ? null : e.UserName;
        if (!string.IsNullOrEmpty(e.ClientId))
            _sessions[e.ClientId] = new SessionOwner(username, null);
        _logger.Debug($"MQTT broker: client '{e.ClientId}' connected ({username ?? "anonymous"}, {e.RemoteEndPoint})");
        _events?.Publish(new MqttBrokerClientConnectedEvent(e.ClientId, username, e.RemoteEndPoint?.ToString() ?? ""));
        return Task.CompletedTask;
    }

    private Task OnClientDisconnectedAsync(ClientDisconnectedEventArgs e)
    {
        var username = string.IsNullOrEmpty(e.UserName) ? null : e.UserName;
        var clean = e.DisconnectType == MqttClientDisconnectType.Clean;
        // Mark the session idle; the sweeper deletes it after the expiry, unless the client is back by then.
        if (_sessions.TryGetValue(e.ClientId, out var owner))
            _sessions.TryUpdate(e.ClientId, owner with { DisconnectedAt = DateTimeOffset.UtcNow }, owner);
        _logger.Debug($"MQTT broker: client '{e.ClientId}' disconnected ({(clean ? "clean" : e.DisconnectType.ToString())})");
        _events?.Publish(new MqttBrokerClientDisconnectedEvent(e.ClientId, username, clean));
        return Task.CompletedTask;
    }

    private Task OnSessionDeletedAsync(SessionDeletedEventArgs e)
    {
        _sessions.TryRemove(e.Id, out _);
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
            _logger.Debug($"MQTT broker: dropped publish by '{e.ClientId}' to '{topic}' (not allowed)");
            return;
        }

        if (e.ApplicationMessage.Retain && !TryReserveRetained(topic, e.ApplicationMessage.Payload.Length, out var limit))
        {
            e.ProcessPublish = false;
            e.Response.ReasonCode = MqttPubAckReasonCode.QuotaExceeded;
            _logger.Warning($"MQTT broker: dropped retained publish by '{e.ClientId}' to '{topic}' ({limit})");
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

    private async Task SweepLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(SessionSweepInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                try
                {
                    await SweepSessionsAsync(DateTimeOffset.UtcNow).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Error($"MQTT broker: session sweep failed: {ex}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stop.
        }
    }

    /// <summary>
    /// Deletes persistent sessions whose client has been away longer than
    /// <see cref="MqttBrokerConfig.PersistentSessionExpiryMinutes"/>, and forgets ids whose session is gone.
    /// </summary>
    internal async Task SweepSessionsAsync(DateTimeOffset now)
    {
        var server = _server;
        if (server is null)
            return;
        var connected = (await server.GetClientsAsync().ConfigureAwait(false)).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var existing = (await server.GetSessionsAsync().ConfigureAwait(false)).ToDictionary(s => s.Id, StringComparer.Ordinal);
        var expiry = Config.PersistentSessionExpiryMinutes > 0 ? TimeSpan.FromMinutes(Config.PersistentSessionExpiryMinutes) : (TimeSpan?)null;

        foreach (var (id, owner) in _sessions.ToArray())
        {
            if (connected.Contains(id))
            {
                if (owner.DisconnectedAt is not null)
                    _sessions.TryUpdate(id, owner with { DisconnectedAt = null }, owner);
                continue;
            }
            if (!existing.TryGetValue(id, out var session))
            {
                _sessions.TryRemove(id, out _);
                continue;
            }
            // MQTTnet records when the client left; our own stamp is the fallback.
            var since = session.DisconnectedTimestamp is { } stamp
                ? new DateTimeOffset(DateTime.SpecifyKind(stamp, DateTimeKind.Utc))
                : owner.DisconnectedAt;
            if (since is null)
            {
                _sessions.TryUpdate(id, owner with { DisconnectedAt = now }, owner);
                continue;
            }
            if (expiry is { } limit && now - since.Value >= limit)
            {
                await session.DeleteAsync().ConfigureAwait(false);
                _sessions.TryRemove(id, out _);
                _logger.Debug($"MQTT broker: expired the persistent session of '{id}' ({owner.Username ?? "anonymous"})");
            }
        }
    }
}
