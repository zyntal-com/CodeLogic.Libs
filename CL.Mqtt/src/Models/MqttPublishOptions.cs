namespace CL.Mqtt.Models;

/// <summary>
/// How a message is published. The defaults (QoS 1, not retained) suit commands and events; use
/// <see cref="Retained"/> for state that late subscribers must see.
/// </summary>
public sealed record MqttPublishOptions
{
    /// <summary>The defaults: QoS 1, not retained.</summary>
    public static MqttPublishOptions Default { get; } = new();

    /// <summary>QoS 1 and retained: the broker keeps the message as the topic's last value.</summary>
    public static MqttPublishOptions Retained { get; } = new() { Retain = true };

    /// <summary>Delivery guarantee.</summary>
    public MqttQos Qos { get; init; } = MqttQos.AtLeastOnce;

    /// <summary>
    /// Ask the broker to keep this message and hand it to every future subscriber of the topic. Publishing an
    /// empty retained payload clears the topic's retained message.
    /// </summary>
    public bool Retain { get; init; }

    /// <summary>MQTT 5 content type, e.g. <c>application/json</c>. Ignored on MQTT 3.1.1.</summary>
    public string? ContentType { get; init; }

    /// <summary>MQTT 5 message expiry: the broker drops the message if it cannot deliver it in time. Ignored on MQTT 3.1.1.</summary>
    public TimeSpan? MessageExpiry { get; init; }

    /// <summary>MQTT 5 response topic for request/response. Ignored on MQTT 3.1.1.</summary>
    public string? ResponseTopic { get; init; }

    /// <summary>MQTT 5 correlation data for request/response. Ignored on MQTT 3.1.1.</summary>
    public ReadOnlyMemory<byte> CorrelationData { get; init; }

    /// <summary>MQTT 5 user properties. Ignored on MQTT 3.1.1.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> UserProperties { get; init; } = [];
}
