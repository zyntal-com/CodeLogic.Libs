using CodeLogic.Core.Configuration;

namespace CL.Mqtt.Models;

/// <summary>
/// Settings of one client connection to an MQTT broker (an entry of <see cref="MqttConfig.Connections"/>).
/// Every setting has a working default for a broker on <c>localhost:1883</c>.
/// </summary>
public sealed class MqttConnectionConfig
{
    /// <summary>Turn this connection on or off without removing it.</summary>
    [ConfigField(Label = "Enabled", Group = "General", Order = 0)]
    public bool Enabled { get; set; } = true;

    /// <summary>Broker host name or IP address.</summary>
    [ConfigField(Label = "Host", Required = true, Placeholder = "localhost", Group = "Broker", Order = 1)]
    public string Host { get; set; } = "localhost";

    /// <summary>Broker port: 1883 is plain MQTT, 8883 is MQTT over TLS.</summary>
    [ConfigField(Label = "Port", Min = 1, Max = 65535, Group = "Broker", Order = 2)]
    public int Port { get; set; } = 1883;

    /// <summary>Connect with TLS.</summary>
    [ConfigField(Label = "Use TLS", Group = "Broker", Order = 3)]
    public bool UseTls { get; set; }

    /// <summary>
    /// Accept any server certificate (self-signed, expired, wrong host). Only for test setups and brokers on a
    /// trusted network: it removes the protection TLS gives against impostors.
    /// </summary>
    [ConfigField(Label = "Allow Untrusted Certificates", Group = "Broker", Order = 4, Collapsed = true)]
    public bool AllowUntrustedCertificates { get; set; }

    /// <summary>MQTT protocol version.</summary>
    [ConfigField(Label = "Protocol", Description = "V311 works with every broker; V500 adds reason codes and properties.", Group = "Broker", Order = 5)]
    public MqttProtocol Protocol { get; set; } = MqttProtocol.V311;

    /// <summary>User name; empty to connect without credentials.</summary>
    [ConfigField(Label = "Username", Group = "Credentials", Order = 10)]
    public string? Username { get; set; }

    /// <summary>Password; only sent with a user name.</summary>
    [ConfigField(Label = "Password", InputType = ConfigInputType.Password, Secret = true, Group = "Credentials", Order = 11)]
    public string? Password { get; set; }

    /// <summary>
    /// The client id the broker knows this client by. Empty generates a stable id from the machine and
    /// connection name (<c>cl-{machine}-{connection}</c>). Two clients with the same id disconnect each other.
    /// </summary>
    [ConfigField(Label = "Client ID", Group = "Session", Order = 20)]
    public string? ClientId { get; set; }

    /// <summary>
    /// Start with a clean session. When false the broker keeps the subscriptions and queues QoS 1/2 messages
    /// while the client is away (needs a stable <see cref="ClientId"/>).
    /// </summary>
    [ConfigField(Label = "Clean Session", Group = "Session", Order = 21)]
    public bool CleanSession { get; set; } = true;

    /// <summary>Seconds between keep-alive pings; the broker drops the client after 1.5 × this without traffic.</summary>
    [ConfigField(Label = "Keep Alive (s)", Min = 5, Max = 3600, Group = "Session", Order = 22, Collapsed = true)]
    public int KeepAliveSeconds { get; set; } = 30;

    /// <summary>Seconds to wait for the broker to accept a connection, a publish or a subscription.</summary>
    [ConfigField(Label = "Timeout (s)", Min = 1, Max = 300, Group = "Session", Order = 23, Collapsed = true)]
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>Connect when the library starts. When false, call <c>ConnectAsync</c> yourself.</summary>
    [ConfigField(Label = "Auto Connect", Group = "Reconnect", Order = 30)]
    public bool AutoConnect { get; set; } = true;

    /// <summary>Keep retrying after a failed connect or a lost connection, with exponential back-off.</summary>
    [ConfigField(Label = "Auto Reconnect", Group = "Reconnect", Order = 31)]
    public bool AutoReconnect { get; set; } = true;

    /// <summary>First retry delay in seconds; it doubles on every failed attempt up to <see cref="ReconnectMaxDelaySeconds"/>.</summary>
    [ConfigField(Label = "Reconnect Min Delay (s)", Min = 1, Max = 3600, Group = "Reconnect", Order = 32, Collapsed = true)]
    public int ReconnectMinDelaySeconds { get; set; } = 1;

    /// <summary>Longest delay between retries, in seconds.</summary>
    [ConfigField(Label = "Reconnect Max Delay (s)", Min = 1, Max = 3600, Group = "Reconnect", Order = 33, Collapsed = true)]
    public int ReconnectMaxDelaySeconds { get; set; } = 60;

    /// <summary>
    /// Last-will topic: the broker publishes <see cref="WillPayload"/> here when the client disappears without
    /// disconnecting. Empty for no will. Typical use: <c>myapp/status</c> with payload <c>offline</c>.
    /// </summary>
    [ConfigField(Label = "Will Topic", Group = "Last Will", Order = 40, Collapsed = true)]
    public string? WillTopic { get; set; }

    /// <summary>Last-will payload (UTF-8 text).</summary>
    [ConfigField(Label = "Will Payload", Group = "Last Will", Order = 41, Collapsed = true)]
    public string? WillPayload { get; set; }

    /// <summary>Publish the last will as a retained message.</summary>
    [ConfigField(Label = "Will Retain", Group = "Last Will", Order = 42, Collapsed = true)]
    public bool WillRetain { get; set; }

    /// <summary>Checks this connection; <paramref name="path"/> prefixes every error path (e.g. <c>connections.Default</c>).</summary>
    public IEnumerable<ConfigFieldError> Validate(string path)
    {
        if (string.IsNullOrWhiteSpace(Host))
            yield return new($"{path}.host", "Host is required.");
        if (Port is < 1 or > 65535)
            yield return new($"{path}.port", "Port must be between 1 and 65535.");
        if (!Enum.IsDefined(Protocol))
            yield return new($"{path}.protocol", $"Protocol '{Protocol}' is not a known MQTT version.");
        if (KeepAliveSeconds is < 5 or > 3600)
            yield return new($"{path}.keepAliveSeconds", "KeepAliveSeconds must be between 5 and 3600.");
        if (TimeoutSeconds is < 1 or > 300)
            yield return new($"{path}.timeoutSeconds", "TimeoutSeconds must be between 1 and 300.");
        if (ReconnectMinDelaySeconds is < 1 or > 3600 || ReconnectMaxDelaySeconds is < 1 or > 3600)
            yield return new($"{path}.reconnectMinDelaySeconds", "Reconnect delays must be between 1 and 3600 seconds.");
        else if (ReconnectMinDelaySeconds > ReconnectMaxDelaySeconds)
            yield return new($"{path}.reconnectMinDelaySeconds", "ReconnectMinDelaySeconds cannot be greater than ReconnectMaxDelaySeconds.");
        if (!CleanSession && string.IsNullOrWhiteSpace(ClientId))
            yield return new($"{path}.clientId", "A persistent session (CleanSession = false) needs a fixed ClientId.");
        if (!string.IsNullOrEmpty(WillTopic) && Services.MqttTopic.ValidateTopic(WillTopic) is { } willError)
            yield return new($"{path}.willTopic", willError);
    }
}
