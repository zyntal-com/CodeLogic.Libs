using CodeLogic.Core.Events;

namespace CL.Data.Events;

/// <summary>
/// Base of every event CL.Data publishes on the CodeLogic event bus. The hierarchy is closed, so a
/// <c>switch</c> over a <see cref="DataEvent"/> is checked for exhaustiveness (CS8509 is an error).
/// </summary>
/// <param name="ConnectionId">Name of the configured connection.</param>
/// <param name="Provider">Provider name (<c>mysql</c>, <c>postgresql</c>, <c>sqlserver</c>, <c>sqlite</c>, ...).</param>
/// <param name="Node">Node that served the operation (<c>primary</c> or a replica name); null when not node-specific.</param>
/// <param name="At">When the event happened.</param>
public closed record DataEvent(string ConnectionId, string Provider, string? Node, DateTimeOffset At) : IEvent;

/// <summary>A command finished. Only published when query events are enabled (they are frequent).</summary>
/// <param name="Operation">Statement kind: <c>select</c>, <c>insert</c>, <c>update</c>, <c>delete</c>, <c>upsert</c>, <c>ddl</c>, <c>raw</c>.</param>
/// <param name="Table">Main table, when known.</param>
/// <param name="Duration">Time spent executing, including reading rows.</param>
/// <param name="Rows">Rows read or affected.</param>
/// <param name="Succeeded">False when the command failed.</param>
/// <param name="ConnectionId">Name of the configured connection.</param>
/// <param name="Provider">Provider name.</param>
/// <param name="Node">Node that served the operation.</param>
/// <param name="At">When the event happened.</param>
public sealed record QueryExecutedEvent(string ConnectionId, string Provider, string? Node, DateTimeOffset At,
    string Operation, string? Table, TimeSpan Duration, long Rows, bool Succeeded)
    : DataEvent(ConnectionId, Provider, Node, At);

/// <summary>A command took longer than the connection's slow-query threshold.</summary>
/// <param name="Sql">SQL text with parameter placeholders (never parameter values).</param>
/// <param name="Duration">Execution time.</param>
/// <param name="Plan">Execution plan captured by the provider, when EXPLAIN capture is on.</param>
/// <param name="ConnectionId">Name of the configured connection.</param>
/// <param name="Provider">Provider name.</param>
/// <param name="Node">Node that served the operation.</param>
/// <param name="At">When the event happened.</param>
public sealed record SlowQueryEvent(string ConnectionId, string Provider, string? Node, DateTimeOffset At,
    string Sql, TimeSpan Duration, string? Plan)
    : DataEvent(ConnectionId, Provider, Node, At);

/// <summary>The same query shape ran many times in a short window: a likely N+1 pattern.</summary>
/// <param name="QueryShape">Normalized SQL text of the repeated query.</param>
/// <param name="Count">Executions inside the window.</param>
/// <param name="Window">Detection window.</param>
/// <param name="ConnectionId">Name of the configured connection.</param>
/// <param name="Provider">Provider name.</param>
/// <param name="Node">Node that served the operation.</param>
/// <param name="At">When the event happened.</param>
public sealed record NPlusOneDetectedEvent(string ConnectionId, string Provider, string? Node, DateTimeOffset At,
    string QueryShape, int Count, TimeSpan Window)
    : DataEvent(ConnectionId, Provider, Node, At);

/// <summary>A read was answered from the cache.</summary>
/// <param name="Table">Main table of the cached query.</param>
/// <param name="ConnectionId">Name of the configured connection.</param>
/// <param name="Provider">Provider name.</param>
/// <param name="At">When the event happened.</param>
public sealed record CacheHitEvent(string ConnectionId, string Provider, DateTimeOffset At, string? Table)
    : DataEvent(ConnectionId, Provider, null, At);

/// <summary>A cacheable read missed the cache and went to the database.</summary>
/// <param name="Table">Main table of the cached query.</param>
/// <param name="ConnectionId">Name of the configured connection.</param>
/// <param name="Provider">Provider name.</param>
/// <param name="At">When the event happened.</param>
public sealed record CacheMissEvent(string ConnectionId, string Provider, DateTimeOffset At, string? Table)
    : DataEvent(ConnectionId, Provider, null, At);

/// <summary>Schema synchronization changed (or created) a table.</summary>
/// <param name="Table">Table name.</param>
/// <param name="Created">True when the table was created.</param>
/// <param name="ChangesApplied">Number of schema changes applied.</param>
/// <param name="ConnectionId">Name of the configured connection.</param>
/// <param name="Provider">Provider name.</param>
/// <param name="At">When the event happened.</param>
public sealed record SchemaSyncedEvent(string ConnectionId, string Provider, DateTimeOffset At,
    string Table, bool Created, int ChangesApplied)
    : DataEvent(ConnectionId, Provider, "primary", At);

/// <summary>Schema synchronization found changes the sync mode does not allow it to apply.</summary>
/// <param name="Table">Table name.</param>
/// <param name="PendingChanges">Human-readable list of the changes left pending.</param>
/// <param name="ConnectionId">Name of the configured connection.</param>
/// <param name="Provider">Provider name.</param>
/// <param name="At">When the event happened.</param>
public sealed record SchemaDriftDetectedEvent(string ConnectionId, string Provider, DateTimeOffset At,
    string Table, IReadOnlyList<string> PendingChanges)
    : DataEvent(ConnectionId, Provider, "primary", At);

/// <summary>A migration was applied or rolled back.</summary>
/// <param name="Version">Migration version.</param>
/// <param name="Name">Migration name.</param>
/// <param name="RolledBack">True for a rollback.</param>
/// <param name="Duration">Time taken.</param>
/// <param name="ConnectionId">Name of the configured connection.</param>
/// <param name="Provider">Provider name.</param>
/// <param name="At">When the event happened.</param>
public sealed record MigrationAppliedEvent(string ConnectionId, string Provider, DateTimeOffset At,
    string Version, string Name, bool RolledBack, TimeSpan Duration)
    : DataEvent(ConnectionId, Provider, "primary", At);

/// <summary>A seed ran.</summary>
/// <param name="Seed">Seed name.</param>
/// <param name="ConnectionId">Name of the configured connection.</param>
/// <param name="Provider">Provider name.</param>
/// <param name="At">When the event happened.</param>
public sealed record SeedAppliedEvent(string ConnectionId, string Provider, DateTimeOffset At, string Seed)
    : DataEvent(ConnectionId, Provider, "primary", At);

/// <summary>The retention worker deleted expired rows.</summary>
/// <param name="Table">Table name.</param>
/// <param name="Rows">Rows deleted in this run.</param>
/// <param name="ConnectionId">Name of the configured connection.</param>
/// <param name="Provider">Provider name.</param>
/// <param name="At">When the event happened.</param>
public sealed record RetentionPurgedEvent(string ConnectionId, string Provider, DateTimeOffset At, string Table, long Rows)
    : DataEvent(ConnectionId, Provider, "primary", At);

/// <summary>A bulk copy finished.</summary>
/// <param name="Table">Target table.</param>
/// <param name="Rows">Rows copied.</param>
/// <param name="Duration">Time taken.</param>
/// <param name="ConnectionId">Name of the configured connection.</param>
/// <param name="Provider">Provider name.</param>
/// <param name="At">When the event happened.</param>
public sealed record BulkCopyCompletedEvent(string ConnectionId, string Provider, DateTimeOffset At,
    string Table, long Rows, TimeSpan Duration)
    : DataEvent(ConnectionId, Provider, "primary", At);

/// <summary>A node became healthy or unhealthy. Published on transitions only.</summary>
/// <param name="Healthy">New state.</param>
/// <param name="Message">Reason, e.g. the connection error or the replica lag.</param>
/// <param name="ConnectionId">Name of the configured connection.</param>
/// <param name="Provider">Provider name.</param>
/// <param name="Node">Node that served the operation.</param>
/// <param name="At">When the event happened.</param>
public sealed record NodeHealthChangedEvent(string ConnectionId, string Provider, string Node, DateTimeOffset At,
    bool Healthy, string? Message)
    : DataEvent(ConnectionId, Provider, Node, At);

/// <summary>The circuit breaker of a node changed state.</summary>
/// <param name="State">New state: <c>closed</c>, <c>open</c> or <c>half-open</c>.</param>
/// <param name="ConnectionId">Name of the configured connection.</param>
/// <param name="Provider">Provider name.</param>
/// <param name="Node">Node that served the operation.</param>
/// <param name="At">When the event happened.</param>
public sealed record CircuitStateChangedEvent(string ConnectionId, string Provider, string Node, DateTimeOffset At, string State)
    : DataEvent(ConnectionId, Provider, Node, At);
