using System.Buffers;
using System.Text;
using CL.Mqtt.Models;
using MQTTnet;
using MQTTnet.Protocol;

namespace CL.Mqtt.Services;

/// <summary>Converts between CL.Mqtt's types and MQTTnet's, so MQTTnet types never appear in the public API.</summary>
internal static class MqttMessageMapper
{
    public static MqttQualityOfServiceLevel ToNative(MqttQos qos) => qos switch
    {
        MqttQos.AtMostOnce => MqttQualityOfServiceLevel.AtMostOnce,
        MqttQos.AtLeastOnce => MqttQualityOfServiceLevel.AtLeastOnce,
        MqttQos.ExactlyOnce => MqttQualityOfServiceLevel.ExactlyOnce,
        _ => throw new ArgumentOutOfRangeException(nameof(qos), qos, "Unknown QoS level.")
    };

    public static MqttQos FromNative(MqttQualityOfServiceLevel qos) => qos switch
    {
        MqttQualityOfServiceLevel.AtLeastOnce => MqttQos.AtLeastOnce,
        MqttQualityOfServiceLevel.ExactlyOnce => MqttQos.ExactlyOnce,
        _ => MqttQos.AtMostOnce
    };

    /// <summary>Builds an MQTTnet message to publish. The topic must already be validated.</summary>
    public static MqttApplicationMessage ToNative(string topic, ReadOnlyMemory<byte> payload, MqttPublishOptions options)
    {
        var builder = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(new ReadOnlySequence<byte>(payload))
            .WithQualityOfServiceLevel(ToNative(options.Qos))
            .WithRetainFlag(options.Retain);

        if (!string.IsNullOrEmpty(options.ContentType))
            builder.WithContentType(options.ContentType);
        if (options.MessageExpiry is { } expiry)
            builder.WithMessageExpiryInterval((uint)Math.Clamp(expiry.TotalSeconds, 1, uint.MaxValue));
        if (!string.IsNullOrEmpty(options.ResponseTopic))
            builder.WithResponseTopic(options.ResponseTopic);
        if (!options.CorrelationData.IsEmpty)
            builder.WithCorrelationData(options.CorrelationData.ToArray());
        foreach (var (name, value) in options.UserProperties)
            builder.WithUserProperty(name, Encoding.UTF8.GetBytes(value).AsMemory());
        return builder.Build();
    }

    /// <summary>Copies an MQTTnet message into an <see cref="MqttMessage"/> the handler may keep.</summary>
    public static MqttMessage FromNative(MqttApplicationMessage message, string? senderClientId = null, string? senderUserName = null) =>
        new(message.Topic, message.Payload.ToArray(), FromNative(message.QualityOfServiceLevel), message.Retain)
        {
            ContentType = message.ContentType,
            ResponseTopic = message.ResponseTopic,
            CorrelationData = message.CorrelationData ?? ReadOnlyMemory<byte>.Empty,
            UserProperties = message.UserProperties is { Count: > 0 } props
                ? [.. props.Select(p => new KeyValuePair<string, string>(p.Name, Encoding.UTF8.GetString(p.ValueBuffer.Span)))]
                : [],
            SenderClientId = senderClientId,
            SenderUserName = senderUserName
        };
}
