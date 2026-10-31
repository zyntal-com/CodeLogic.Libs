using CL.Data.Errors;
using CL.Data.Execution;
using CL.Data.Mapping;
using CL.Data.Sql;
using CL.Data.Translation;
using CL.Data.Writes;
using CodeLogic.Core.Results;

namespace CL.Data.Query;

/// <summary>A page of results.</summary>
/// <typeparam name="T">Row type.</typeparam>
/// <param name="Items">Rows on this page.</param>
/// <param name="Page">1-based page number.</param>
/// <param name="PageSize">Requested page size.</param>
/// <param name="TotalCount">Total rows, when requested (<c>withTotal</c>); null otherwise.</param>
/// <param name="HasMore">True when a later page has rows.</param>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long? TotalCount, bool HasMore)
{
    /// <summary>Total pages, when the total was requested.</summary>
    public int? TotalPages => TotalCount is { } total ? (int)Math.Ceiling(total / (double)Math.Max(1, PageSize)) : null;
}

/// <summary>
/// Builds the SELECTs for an entity with its standing filters: soft-deleted rows are hidden unless asked for,
/// and tenant-scoped entities only see the current tenant's rows. Every read path goes through here.
/// </summary>
internal static class ReadPlanner
{
    /// <summary>A SELECT of all mapped columns of <paramref name="map"/>, filtered.</summary>
    public static Result<SelectStatement> Select(Database db, EntityMap map, SqlExpr? where, bool includeDeleted, string? alias = null,
        FilterSelection ignore = default)
    {
        var filters = Filters(db, map, includeDeleted, alias, ignore);
        if (filters.IsFailure) return filters.Error!;

        var names = map.GetNames(db.NamingPolicy);
        var table = WritePlanner.TableOf(map, names, db.WriteContext) with { Alias = alias };
        return new SelectStatement(table)
        {
            Projection = map.Columns.Select(c => new ProjectionItem(new ColumnExpr(names.Of(c), alias))).ToArray(),
            Where = And(where, filters.Value),
        };
    }

    /// <summary>The standing filters of <paramref name="map"/> (soft delete, tenant, user filters), or null when there are none.</summary>
    public static Result<SqlExpr?> Filters(Database db, EntityMap map, bool includeDeleted, string? alias, FilterSelection ignore = default)
    {
        var names = map.GetNames(db.NamingPolicy);
        SqlExpr? filter = null;
        if (!includeDeleted && map.SoftDelete is { } soft)
            filter = new IsNullExpr(new ColumnExpr(names.Of(soft), alias));

        if (map.TenantKey is { } tenant)
        {
            var current = db.CurrentTenant;
            if (current is null) return DataErrors.TenantMissing(map.ClrType.Name);
            filter = And(filter, new BinaryExpr(SqlBinaryOperator.Equal, new ColumnExpr(names.Of(tenant), alias),
                new ParameterExpr(SqlParameterValue.ForColumn(current, tenant))));
        }

        foreach (var user in db.Options.Filters)
        {
            if (!user.AppliesTo(map.ClrType) || ignore.Ignores(user.Name)) continue;
            var scope = new TranslationScope(db.Provider.Dialect, db.NamingPolicy).Bind(user.Predicate.Parameters[0], map, alias);
            var condition = SqlTranslator.TranslatePredicate(user.Predicate, scope);
            if (condition.IsFailure)
                return DataErrors.TranslationFailed($"Filter '{user.Name}' cannot be applied to '{map.ClrType.Name}'.", condition.Error!.Message);
            filter = And(filter, condition.Value);
        }
        return Result<SqlExpr?>.Success(filter);
    }

    /// <summary><c>key = @k</c> for <paramref name="key"/>, validating its size.</summary>
    public static Result<SqlExpr> KeyPredicate(EntityMap map, EntityNames names, EntityKey key)
    {
        if (map.Key.IsEmpty) return DataErrors.NoPrimaryKey(map.ClrType.Name);
        if (key.Count != map.Key.Columns.Count)
            return DataErrors.Validation($"'{map.ClrType.Name}' has a key of {map.Key.Columns.Count} column(s); {key.Count} value(s) were given.");
        return WritePlanner.KeyPredicate(map, names, key);
    }

    /// <summary>ORDER BY the primary key (stable paging when the caller gave no order).</summary>
    public static IReadOnlyList<OrderItem> KeyOrder(EntityMap map, EntityNames names, string? alias = null) =>
        map.Key.Columns.Select(c => new OrderItem(new ColumnExpr(names.Of(c), alias))).ToArray();

    public static SqlExpr? And(SqlExpr? left, SqlExpr? right) =>
        left is null ? right : right is null ? left : new BinaryExpr(SqlBinaryOperator.And, left, right);
}
