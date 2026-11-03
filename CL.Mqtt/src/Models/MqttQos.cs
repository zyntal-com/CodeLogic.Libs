namespace CL.Mqtt.Models;

/// <summary>
/// MQTT quality of service: how hard the broker and client try to deliver a message.
/// The numeric values match the protocol (0, 1, 2).
/// </summary>
public enum MqttQos
{
    /// <summary>QoS 0: sent once, no acknowledgement. Cheapest; a message can be lost.</summary>
    AtMostOnce = 0,

    /// <summary>QoS 1: acknowledged and re-sent until acknowledged. Never lost, but can arrive twice.</summary>
    AtLeastOnce = 1,

    /// <summary>QoS 2: a four-step handshake so the message arrives exactly once. Slowest.</summary>
    ExactlyOnce = 2
}
