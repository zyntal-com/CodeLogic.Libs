using System.Collections.Concurrent;
using CL.PostgreSQL.Configuration;
using Npgsql;

namespace CL.PostgreSQL.Core;

/// <summary>
/// Process-wide, per-connection-id snapshot of the configuration knobs that the query,
/// schema and cache pipelines need but cannot reach through a constructor — most notably
/// <see cref="PostgreSqlDatabaseConfig.DefaultSchema"/>, which has to be visible to the
/// static <see cref="EntityMetadata{T}"/> cache.
/// <para>
/// Populated by <c>ConnectionManager.RegisterConfiguration</c>, which every registration
/// path funnels through, so a connection registered at runtime is honoured just like one
/// declared in <c>config.postgresql.json</c>. Unregistered connection IDs resolve to
/// <see cref="Defaults"/>, which reproduces the library's historical behaviour exactly.
/// </para>
/// </summary>
internal static class PostgreSqlRuntimeOptions
{
    /// <summary>Resolved knobs for one connection ID.</summary>
    internal sealed record Entry(
        string DefaultSchema,
        int DefaultStringSize,
        int QueryTimeoutMs,
        int N1DetectorThreshold,
        bool CaptureExplainOnSlowQuery,
        bool? CacheEnabledOverride,
        int MaxInClauseValues,
        string? BackupDirectory);

    /// <summary>
    /// What an unregistered connection resolves to. Every value matches the corresponding
    /// <see cref="PostgreSqlDatabaseConfig"/> default, so nothing changes for a caller that
    /// never registered a configuration (unit tests, direct <c>SchemaAnalyzer</c> use).
    /// </summary>
    public static readonly Entry Defaults = new(
        DefaultSchema: PostgreSqlDialect.DefaultSchema,
        DefaultStringSize: 255,
        QueryTimeoutMs: 30_000,
        N1DetectorThreshold: 0,
        CaptureExplainOnSlowQuery: false,
        CacheEnabledOverride: null,
        MaxInClauseValues: 1_000,
        BackupDirectory: null);

    private const string DefaultConnectionId = "Default";

    private static readonly ConcurrentDictionary<string, Entry> _byConnection =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when at least one registered connection has the N+1 detector switched on.
    /// Read on the hot path before any dictionary lookup so a disabled detector costs a
    /// single volatile bool read and allocates nothing.
    /// </summary>
    private static volatile bool _anyN1Enabled;

    /// <summary>See <see cref="_anyN1Enabled"/>.</summary>
    public static bool AnyN1Enabled => _anyN1Enabled;

    /// <summary>
    /// True when at least one registered connection asks for EXPLAIN capture. Same
    /// rationale as <see cref="AnyN1Enabled"/>.
    /// </summary>
    private static volatile bool _anyExplainEnabled;

    /// <summary>See <see cref="_anyExplainEnabled"/>.</summary>
    public static bool AnyExplainEnabled => _anyExplainEnabled;

    /// <summary>
    /// Captures the knobs for <paramref name="connectionId"/>. Idempotent; a later call
    /// replaces the entry.
    /// </summary>
    public static void Register(string connectionId, PostgreSqlDatabaseConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var schema = string.IsNullOrWhiteSpace(config.DefaultSchema)
            ? PostgreSqlDialect.DefaultSchema
            : config.DefaultSchema.Trim();
        RequireSafeSchema(schema, connectionId);

        _byConnection[connectionId] = new Entry(
            DefaultSchema: schema,
            DefaultStringSize: config.DefaultStringSize > 0 ? config.DefaultStringSize : 255,
            QueryTimeoutMs: Math.Max(0, config.QueryTimeoutMs),
            N1DetectorThreshold: Math.Max(0, config.N1DetectorThreshold),
            CaptureExplainOnSlowQuery: config.CaptureExplainOnSlowQuery,
            CacheEnabledOverride: config.CacheEnabledOverride,
            MaxInClauseValues: Math.Max(1, config.MaxInClauseValues),
            BackupDirectory: string.IsNullOrWhiteSpace(config.BackupDirectory) ? null : config.BackupDirectory);

        _anyN1Enabled = _byConnection.Values.Any(e => e.N1DetectorThreshold > 0);
        _anyExplainEnabled = _byConnection.Values.Any(e => e.CaptureExplainOnSlowQuery);

        // A changed default schema changes every QualifiedTableName derived from it.
        EntityMetadataSchemaCache.Invalidate();
    }

    /// <summary>Drops every registered entry. Called when the library stops.</summary>
    /// <summary>Drops one connection's settings (live config change removed or disabled it).</summary>
    public static void Unregister(string connectionId)
    {
        if (!_byConnection.TryRemove(connectionId, out _)) return;
        _anyN1Enabled = _byConnection.Values.Any(e => e.N1DetectorThreshold > 0);
        _anyExplainEnabled = _byConnection.Values.Any(e => e.CaptureExplainOnSlowQuery);
        EntityMetadataSchemaCache.Invalidate();
    }

    public static void Reset()
    {
        _byConnection.Clear();
        _anyN1Enabled = false;
        _anyExplainEnabled = false;
        EntityMetadataSchemaCache.Invalidate();
    }

    /// <summary>
    /// Resolves the knobs for a connection ID. A null / unknown ID falls back to the
    /// <c>Default</c> connection when one is registered, and to <see cref="Defaults"/>
    /// otherwise.
    /// </summary>
    public static Entry For(string? connectionId)
    {
        if (connectionId is not null && _byConnection.TryGetValue(connectionId, out var entry))
            return entry;
        if (_byConnection.TryGetValue(DefaultConnectionId, out var fallback))
            return fallback;
        return Defaults;
    }

    /// <summary>The configured default schema for a connection (never the explicit per-entity one).</summary>
    public static string DefaultSchemaFor(string? connectionId) => For(connectionId).DefaultSchema;

    /// <summary>
    /// Applies the connection's <c>QueryTimeoutMs</c> to a freshly created command, rounding
    /// up to whole seconds (the unit ADO.NET exposes). 0 means "no timeout" and is passed
    /// through as-is.
    /// </summary>
    public static void ApplyCommandTimeout(NpgsqlCommand cmd, string? connectionId)
    {
        var ms = For(connectionId).QueryTimeoutMs;
        if (ms <= 0) { cmd.CommandTimeout = 0; return; }
        cmd.CommandTimeout = (int)Math.Min(int.MaxValue, Math.Max(1, (ms + 999) / 1000));
    }

    private static void RequireSafeSchema(string schema, string connectionId)
    {
        foreach (var c in schema)
        {
            if (c is '"' or '\0' or '\n' or '\r')
                throw new InvalidOperationException(
                    $"Connection '{connectionId}' declares DefaultSchema '{schema}', which contains a " +
                    "character that is not permitted in a PostgreSQL identifier.");
        }
    }
}

/// <summary>
/// Generation counter for the per-connection schema names cached inside
/// <see cref="EntityMetadata{T}"/>. Bumping it invalidates every type's cache without
/// needing a registry of closed generic types.
/// </summary>
internal static class EntityMetadataSchemaCache
{
    private static int _generation;

    /// <summary>Current generation. Cached qualified names are stamped with it.</summary>
    public static int Generation => Volatile.Read(ref _generation);

    /// <summary>Invalidates every cached per-connection qualified name.</summary>
    public static void Invalidate() => Interlocked.Increment(ref _generation);
}

/// <summary>Command-creation helpers that apply the per-connection query timeout.</summary>
internal static class NpgsqlConnectionExtensions
{
    /// <summary>
    /// Creates a command on <paramref name="conn"/> and applies the configured
    /// <c>QueryTimeoutMs</c> for <paramref name="connectionId"/> as its command timeout.
    /// </summary>
    public static NpgsqlCommand CreateCommand(this NpgsqlConnection conn, string? connectionId)
    {
        var cmd = conn.CreateCommand();
        PostgreSqlRuntimeOptions.ApplyCommandTimeout(cmd, connectionId);
        return cmd;
    }
}
