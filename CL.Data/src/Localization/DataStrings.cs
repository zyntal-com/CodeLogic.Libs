using CodeLogic.Core.Localization;

namespace CL.Data.Localization;

/// <summary>
/// Localizable log and status messages of CL.Data (section <c>data</c>). Placeholders use
/// <see cref="string.Format(string, object?[])"/> numbering.
/// </summary>
[LocalizationSection("data")]
public sealed class DataStrings : LocalizationModelBase
{
    /// <summary>Connection test succeeded. {0} = connection, {1} = node.</summary>
    [LocalizedString(Description = "Logged when a connection test succeeds. {0} = connection, {1} = node.")]
    public string ConnectionTestSucceeded { get; set; } = "Connection '{0}' ({1}) is reachable";

    /// <summary>Connection test failed. {0} = connection, {1} = node, {2} = reason.</summary>
    [LocalizedString(Description = "Logged when a connection test fails. {0} = connection, {1} = node, {2} = reason.")]
    public string ConnectionTestFailed { get; set; } = "Connection '{0}' ({1}) is not reachable: {2}";

    /// <summary>A connection names an unregistered provider. {0} = connection, {1} = provider.</summary>
    [LocalizedString(Description = "Logged when a connection names a provider that is not loaded. {0} = connection, {1} = provider.")]
    public string ProviderMissing { get; set; } = "Connection '{0}' uses provider '{1}', which is not loaded";

    /// <summary>The library has no enabled connections.</summary>
    [LocalizedString(Description = "Logged when no connection is enabled.")]
    public string NoConnections { get; set; } = "No database connections are enabled";

    /// <summary>Library initialized. {0} = number of connections.</summary>
    [LocalizedString(Description = "Logged after initialization. {0} = number of connections.")]
    public string Initialized { get; set; } = "CL.Data initialized with {0} connection(s)";

    /// <summary>Library stopped.</summary>
    [LocalizedString(Description = "Logged when the library stops.")]
    public string Stopped { get; set; } = "CL.Data stopped";

    /// <summary>Configuration reloaded for a connection. {0} = connection.</summary>
    [LocalizedString(Description = "Logged when a connection is rebuilt after a configuration change. {0} = connection.")]
    public string ConnectionReloaded { get; set; } = "Connection '{0}' reloaded with new settings";

    /// <summary>Slow query. {0} = connection, {1} = milliseconds, {2} = SQL.</summary>
    [LocalizedString(Description = "Logged for a slow query. {0} = connection, {1} = milliseconds, {2} = SQL text.")]
    public string SlowQuery { get; set; } = "Slow query on '{0}' ({1} ms): {2}";

    /// <summary>Slow query with its plan. {0} = connection, {1} = milliseconds, {2} = SQL, {3} = execution plan.</summary>
    [LocalizedString(Description = "Logged for a slow query whose plan was captured. {0} = connection, {1} = milliseconds, {2} = SQL text, {3} = plan.")]
    public string SlowQueryWithPlan { get; set; } = "Slow query on '{0}' ({1} ms): {2}\nPlan:\n{3}";

    /// <summary>Likely N+1. {0} = connection, {1} = count, {2} = SQL shape.</summary>
    [LocalizedString(Description = "Logged when the same query repeats many times. {0} = connection, {1} = count, {2} = SQL shape.")]
    public string NPlusOne { get; set; } = "Possible N+1 on '{0}': query ran {1} times in a short window: {2}";

    /// <summary>Table created. {0} = table.</summary>
    [LocalizedString(Description = "Logged when schema sync creates a table. {0} = table.")]
    public string TableCreated { get; set; } = "Created table {0}";

    /// <summary>Table changed. {0} = table, {1} = number of changes.</summary>
    [LocalizedString(Description = "Logged when schema sync alters a table. {0} = table, {1} = number of changes.")]
    public string TableAltered { get; set; } = "Updated table {0} ({1} change(s))";

    /// <summary>Schema drift left pending. {0} = table, {1} = changes.</summary>
    [LocalizedString(Description = "Logged when schema sync leaves changes pending. {0} = table, {1} = list of changes.")]
    public string SchemaDrift { get; set; } = "Table {0} differs from its model; pending: {1}";

    /// <summary>Schema sync failed. {0} = table, {1} = reason.</summary>
    [LocalizedString(Description = "Logged when schema sync fails. {0} = table, {1} = reason.")]
    public string SchemaSyncFailed { get; set; } = "Schema synchronization failed for {0}: {1}";

    /// <summary>Migration applied. {0} = version, {1} = name.</summary>
    [LocalizedString(Description = "Logged after a migration runs. {0} = version, {1} = name.")]
    public string MigrationApplied { get; set; } = "Applied migration {0} {1}";

    /// <summary>Migration rolled back. {0} = version, {1} = name.</summary>
    [LocalizedString(Description = "Logged after a migration is rolled back. {0} = version, {1} = name.")]
    public string MigrationRolledBack { get; set; } = "Rolled back migration {0} {1}";

    /// <summary>Retention purge. {0} = rows, {1} = table.</summary>
    [LocalizedString(Description = "Logged after the retention worker deletes rows. {0} = rows, {1} = table.")]
    public string RetentionPurged { get; set; } = "Retention deleted {0} expired row(s) from {1}";

    /// <summary>Node health changed. {0} = connection, {1} = node, {2} = state, {3} = reason.</summary>
    [LocalizedString(Description = "Logged when a node's health changes. {0} = connection, {1} = node, {2} = healthy/unhealthy, {3} = reason.")]
    public string NodeHealthChanged { get; set; } = "Node {1} of '{0}' is now {2}: {3}";
}
