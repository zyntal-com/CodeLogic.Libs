using CL.Data.Mapping;
using CL.Data.Providers;
using CL.Data.Sql;

namespace CL.Data.Writes;

/// <summary>Supplies the current user for <c>[CreatedBy]</c>/<c>[UpdatedBy]</c> columns.</summary>
public interface IDataUserAccessor
{
    /// <summary>The current user's identifier, or null when unknown.</summary>
    string? CurrentUser { get; }
}

/// <summary>Supplies the current tenant for <c>[TenantKey]</c> entities.</summary>
public interface IDataTenantAccessor
{
    /// <summary>The current tenant's key, or null when there is none.</summary>
    object? CurrentTenant { get; }
}

/// <summary>Everything the write planner needs besides the entities: SQL dialect, limits, clock, user and tenant.</summary>
public sealed record WriteContext
{
    /// <summary>SQL dialect.</summary>
    public required SqlDialect Dialect { get; init; }

    /// <summary>Provider capabilities (parameter limit, RETURNING).</summary>
    public required ProviderCapabilities Capabilities { get; init; }

    /// <summary>Naming policy of the connection.</summary>
    public NamingPolicy Policy { get; init; } = NamingPolicy.AsIs;

    /// <summary>Schema for tables without an explicit one.</summary>
    public string? DefaultSchema { get; init; }

    /// <summary>Most rows per multi-row statement (the parameter limit may lower it).</summary>
    public int MaxBatchSize { get; init; } = 1000;

    /// <summary>Clock for audit timestamps and soft deletes.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Current user for audit columns.</summary>
    public string? User { get; init; }

    /// <summary>Current tenant for tenant-scoped entities.</summary>
    public object? Tenant { get; init; }
}
