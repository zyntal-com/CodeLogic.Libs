using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CL.Mqtt.Models;
using CodeLogic.Core.Results;
using MQTTnet;
using MQTTnet.Exceptions;

namespace CL.Mqtt.Services;

/// <summary>Publish, subscribe and message dispatch of <see cref="MqttClientService"/>.</summary>
public sealed partial class MqttClientService
{
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

    // ── Receive ───────────────────────────────────────────────────────────────

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

    private static MqttPublishOptions WithJsonContentType(MqttPublishOptions? options) =>
        options is { ContentType: not null } ? options : (options ?? MqttPublishOptions.Default) with { ContentType = "application/json" };
}
