using CodeLogic.Core.Configuration;

namespace CL.Data.Configuration;

/// <summary>
/// Configuration section <c>data</c>: the named database connections. Each connection picks its
/// provider (<c>mysql</c>, <c>postgresql</c>, <c>sqlserver</c>, <c>sqlite</c>, or a third-party name).
/// </summary>
/// <example>
/// <code>
/// "data": {
///   "defaultConnection": "main",
///   "connections": {
///     "main":  { "provider": "mysql", "host": "db1", "database": "app", "username": "app", "password": "...",
///                "replicas": [ { "name": "r1", "host": "db2" } ], "readPreference": "preferReplica" },
///     "local": { "provider": "sqlite", "database": "data/local.db" }
///   }
/// }
/// </code>
/// </example>
[ConfigSection("data")]
public sealed class DataConfig : ConfigModelBase
{
    /// <summary>Master switch. When false, no connection is opened.</summary>
    [ConfigField(Label = "Enabled", Description = "Turn all database connections on or off.", Group = "General", Order = 0)]
    public bool Enabled { get; set; } = true;

    /// <summary>Name of the connection used by <c>Db()</c> without a name.</summary>
    [ConfigField(Label = "Default Connection", Description = "Connection used when no name is given.", Group = "General", Order = 1)]
    public string DefaultConnection { get; set; } = "Default";

    /// <summary>Connections by name (case-insensitive, also after the configuration is loaded from JSON).</summary>
    [ConfigField(Label = "Connections", Group = "Connections", Order = 10)]
    public Dictionary<string, DataConnectionConfig> Connections
    {
        get;
        // A deserializer assigns a fresh dictionary with the default comparer; keep the lookup case-insensitive.
        set => field = value is null ? [with(StringComparer.OrdinalIgnoreCase)] : new(value, StringComparer.OrdinalIgnoreCase);
    } = [with(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Enabled connections, by name.</summary>
    public IEnumerable<KeyValuePair<string, DataConnectionConfig>> EnabledConnections =>
        Enabled ? Connections.Where(kv => kv.Value.Enabled) : [];

    /// <summary>
    /// Checks the shape of the configuration. Provider-specific rules (for example SQL Server
    /// authentication modes) are checked by each provider when the connection is created.
    /// </summary>
    public override ConfigValidationResult Validate()
    {
        List<ConfigFieldError> errors = [];

        if (Enabled && Connections.Count > 0 && !Connections.ContainsKey(DefaultConnection))
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
                connection.Validate($"connections.{name}", errors);
        }

        return errors.Count == 0 ? ConfigValidationResult.Valid() : ConfigValidationResult.Invalid(errors);
    }
}
