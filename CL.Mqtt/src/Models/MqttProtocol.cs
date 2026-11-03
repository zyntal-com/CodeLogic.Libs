namespace CL.Mqtt.Models;

/// <summary>The MQTT protocol version a client speaks.</summary>
public enum MqttProtocol
{
    /// <summary>MQTT 3.1.1: supported by every broker.</summary>
    V311,

    /// <summary>
    /// MQTT 5.0: adds reason codes on publish and subscribe, user properties, content types, response topics
    /// and message expiry. Use it when the broker supports it.
    /// </summary>
    V500
}
