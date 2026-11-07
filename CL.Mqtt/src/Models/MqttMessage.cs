using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CodeLogic.Core.Results;

namespace CL.Mqtt.Models;

/// <summary>
/// A message received from the broker (by a client subscription) or seen by the embedded broker (by a tap).
/// The payload is a private copy: it stays valid after the handler returns.
/// </summary>
public sealed class MqttMessage
{
    /// <summary>Creates a message.</summary>
    public MqttMessage(string topic, ReadOnlyMemory<byte> payload, MqttQos qos = MqttQos.AtMostOnce, bool retain = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(topic);
        Topic = topic;
        Payload = payload;
        Qos = qos;
        Retain = retain;
    }

    /// <summary>The topic the message was published to.</summary>
    public string Topic { get; }

    /// <summary>The raw payload bytes.</summary>
    public ReadOnlyMemory<byte> Payload { get; }

    /// <summary>The quality of service the message was delivered with.</summary>
    public MqttQos Qos { get; }

    /// <summary>
    /// True when this is a retained message: the broker's stored last value for the topic, delivered because the
    /// subscription was just made, rather than a new publish.
    /// </summary>
    public bool Retain { get; }

    /// <summary>MQTT 5 content type (e.g. <c>application/json</c>), when the sender set one.</summary>
    public string? ContentType { get; init; }

    /// <summary>MQTT 5 response topic for request/response, when the sender set one.</summary>
    public string? ResponseTopic { get; init; }

    /// <summary>MQTT 5 correlation data for request/response; empty when not set.</summary>
    public ReadOnlyMemory<byte> CorrelationData { get; init; }

    /// <summary>MQTT 5 user properties, in the order they were sent (names can repeat).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> UserProperties { get; init; } = [];

    /// <summary>The client id of the sender. Only set for messages seen by the embedded broker.</summary>
    public string? SenderClientId { get; init; }

    /// <summary>The user name of the sender. Only set for messages seen by the embedded broker.</summary>
    public string? SenderUserName { get; init; }

    /// <summary>The payload decoded as UTF-8 text.</summary>
    public string PayloadAsString() => Encoding.UTF8.GetString(Payload.Span);

    /// <summary>Reads the payload as JSON with reflection-based serialization.</summary>
    /// <returns>The value, or <see cref="MqttErrorCodes.InvalidJson"/> when the payload is not valid JSON for <typeparamref name="T"/>.</returns>
    public Result<T> ReadJson<T>(JsonSerializerOptions? options = null)
    {
        try
        {
            var value = JsonSerializer.Deserialize<T>(Payload.Span, options);
            return value is null ? MqttErrors.InvalidJson($"The payload on '{Topic}' is JSON null.") : value;
        }
        catch (JsonException ex)
        {
            return MqttErrors.InvalidJson($"The payload on '{Topic}' is not valid JSON for {typeof(T).Name}.", ex);
        }
    }

    /// <summary>Reads the payload as JSON with source-generated metadata (trimming and Native AOT safe).</summary>
    /// <returns>The value, or <see cref="MqttErrorCodes.InvalidJson"/> when the payload is not valid JSON for <typeparamref name="T"/>.</returns>
    public Result<T> ReadJson<T>(JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        try
        {
            var value = JsonSerializer.Deserialize(Payload.Span, typeInfo);
            return value is null ? MqttErrors.InvalidJson($"The payload on '{Topic}' is JSON null.") : value;
        }
        catch (JsonException ex)
        {
            return MqttErrors.InvalidJson($"The payload on '{Topic}' is not valid JSON for {typeof(T).Name}.", ex);
        }
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Topic} ({Payload.Length} bytes, QoS {(int)Qos}{(Retain ? ", retained" : "")})";
}
