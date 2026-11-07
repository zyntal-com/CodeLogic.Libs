using CodeLogic.Core.Configuration;

namespace CL.Mqtt.Models;

/// <summary>
/// Root configuration for <c>CL.Mqtt</c>, serialized to / from <c>config.mqtt.json</c> in the library's
/// config directory. It holds any number of named client connections and the optional embedded broker.
/// </summary>
/// <example>
/// <code>
/// {
///   "enabled": true,
///   "defaultConnection": "Default",
///   "connections": {
///     "Default": { "host": "localhost", "port": 1883 },
///     "Cloud":   { "host": "mqtt.example.com", "port": 8883, "useTls": true, "username": "app", "password": "secret" }
///   },
///   "broker": { "enabled": true, "port": 1883, "bindAddress": "127.0.0.1", "allowAnonymous": true }
/// }
/// </code>
/// </example>
[ConfigSection("mqtt")]
public sealed class MqttConfig : ConfigModelBase
{
    /// <summary>Master switch. When false, no client connects and the broker does not start.</summary>
    [ConfigField(Label = "Enabled", Description = "Turn all MQTT connections and the embedded broker on or off.", Group = "General", Order = 0)]
    public bool Enabled { get; set; } = true;

    /// <summary>Name of the connection returned by <c>MqttLibrary.Client()</c> without a name.</summary>
    [ConfigField(Label = "Default Connection", Description = "Connection used when no name is given.", Group = "General", Order = 1)]
    public string DefaultConnection { get; set; } = "Default";

    /// <summary>Client connections by name (case-insensitive, also after the configuration is loaded from JSON).</summary>
    [ConfigField(Label = "Connections", Group = "Connections", Order = 10)]
    public Dictionary<string, MqttConnectionConfig> Connections
    {
        get;
        // A deserializer assigns a fresh dictionary with the default comparer; keep the lookup case-insensitive.
        set => field = value is null ? [with(StringComparer.OrdinalIgnoreCase)] : new(value, StringComparer.OrdinalIgnoreCase);
    } = [with(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The embedded broker. Off by default.</summary>
    [ConfigField(Label = "Broker", Group = "Broker", Order = 20)]
    public MqttBrokerConfig Broker { get; set; } = new();

    /// <summary>Enabled connections, by name; empty when the library is disabled.</summary>
    public IEnumerable<KeyValuePair<string, MqttConnectionConfig>> EnabledConnections =>
        Enabled ? Connections.Where(kv => kv.Value is { Enabled: true }) : [];

    /// <summary>Checks the whole configuration: every connection, the default connection name and the broker.</summary>
    public override ConfigValidationResult Validate()
    {
        List<ConfigFieldError> errors = [];
        if (!Enabled)
            return ConfigValidationResult.Valid();

        if (Connections.Count > 0 && !Connections.ContainsKey(DefaultConnection))
            errors.Add(new("defaultConnection", $"Default connection '{DefaultConnection}' is not configured."));

        foreach (var (name, connection) in Connections)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                errors.Add(new("connections", "Connection names cannot be empty."));
                continue;
            }
            if (connection is null)
            {
                errors.Add(new($"connections.{name}", "Connection settings are missing."));
                continue;
            }
            if (connection.Enabled)
                errors.AddRange(connection.Validate($"connections.{name}"));
        }

        if (Broker is null)
            errors.Add(new("broker", "Broker settings are missing."));
        else if (Broker.Enabled)
            errors.AddRange(Broker.Validate("broker"));

        return errors.Count == 0 ? ConfigValidationResult.Valid() : ConfigValidationResult.Invalid(errors);
    }
}
