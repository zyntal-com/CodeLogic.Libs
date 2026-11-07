namespace CL.Mqtt.Models;

/// <summary>Where an <see cref="Services.MqttClientService"/> is in its connection lifecycle.</summary>
public enum MqttConnectionState
{
    /// <summary>Not connected and not trying to connect (never started, or disconnected on purpose).</summary>
    Disconnected,

    /// <summary>A connection attempt is in progress.</summary>
    Connecting,

    /// <summary>Connected to the broker; publish and subscribe work.</summary>
    Connected,

    /// <summary>The connection was lost (or the first attempt failed) and the client is retrying with back-off.</summary>
    Reconnecting
}
