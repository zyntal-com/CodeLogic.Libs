using System.Collections.Concurrent;
using CL.Data.Execution;
using CL.Data.Mapping;
using CL.Data.Sql;
using CL.Data.Writes;

namespace CL.Data.Query;

/// <summary>
/// The rendered SQL of a key lookup (<c>Repository.GetByKeyAsync</c>), built once per entity, dialect, naming policy and
/// schema, so a lookup only binds its key (and the tenant) instead of building and rendering a statement every call. It
/// renders exactly what <see cref="ReadPlanner.Select"/> builds: all columns, key equality, the soft-delete and tenant
/// filters, <c>LIMIT 1</c>. Entities with user filters (<c>DatabaseOptions.Filters</c>) keep the general path, since a filter's
/// SQL may depend on values captured when it runs.
/// </summary>
internal sealed class KeyLookupPlan
{
    private const int TenantSlot = -1;
    private const int ConstantSlot = -2;

    private readonly record struct PlanKey(SqlDialect Dialect, NamingPolicy Policy, string? Schema, bool Schemas, EntityMap Map);

    // A null value marks a combination whose SQL cannot be reused (see Build); it is not tried again.
    private static readonly ConcurrentDictionary<PlanKey, KeyLookupPlan?> Plans = new();

    private readonly string _sql;
    private readonly int[] _slots;                       // per parameter: key column index, TenantSlot or ConstantSlot
    private readonly SqlParameterValue?[] _constants;    // the value of each ConstantSlot (the LIMIT)

    private KeyLookupPlan(string sql, int[] slots, SqlParameterValue?[] constants)
    {
        _sql = sql;
        _slots = slots;
        _constants = constants;
    }

    /// <summary>
    /// The bound lookup of <paramref name="key"/>, or null when this lookup takes the general path (user filters, a null key
    /// value, a tenant entity without a tenant, or SQL that could not be reused).
    /// </summary>
    public static CommandSpec? TryBind(Database db, EntityMap map, EntityKey key)
    {
        if (map.Key.IsEmpty || key.Count != map.Key.Columns.Count) return null;
        for (var i = 0; i < key.Count; i++)
            if (key[i] is null) return null;   // a NULL key renders IS NULL: another statement
        foreach (var filter in db.Options.Filters)
            if (filter.AppliesTo(map.ClrType)) return null;
        var tenant = map.TenantKey is null ? null : db.CurrentTenant;
        if (map.TenantKey is not null && tenant is null) return null;   // the general path reports the missing tenant

        var provider = db.Provider;
        var planKey = new PlanKey(provider.Dialect, db.NamingPolicy, db.DefaultSchema, provider.Capabilities.Schemas, map);
        var plan = Plans.TryGetValue(planKey, out var cached) ? cached : Plans.GetOrAdd(planKey, Build(db, map, key, tenant));
        return plan?.Bind(map, key, tenant);
    }

    private CommandSpec Bind(EntityMap map, EntityKey key, object? tenant)
    {
        var parameters = new SqlParameterValue[_slots.Length];
        for (var i = 0; i < _slots.Length; i++)
        {
            parameters[i] = _slots[i] switch
            {
                TenantSlot => SqlParameterValue.ForColumn(tenant, map.TenantKey),
                ConstantSlot => _constants[i]!,
                var column => SqlParameterValue.ForColumn(key[column], map.Key.Columns[column]),
            };
        }
        return new CommandSpec(_sql, parameters);
    }

    /// <summary>
    /// Builds and renders the lookup once, then maps every rendered parameter back to where its value comes from (by
    /// identity). Null when a parameter cannot be accounted for, so the SQL is never reused with wrong values.
    /// </summary>
    private static KeyLookupPlan? Build(Database db, EntityMap map, EntityKey key, object? tenant)
    {
        var names = map.GetNames(db.NamingPolicy);
        var keyParameters = new SqlParameterValue[map.Key.Columns.Count];
        SqlExpr? where = null;
        for (var i = 0; i < keyParameters.Length; i++)
        {
            var column = map.Key.Columns[i];
            keyParameters[i] = SqlParameterValue.ForColumn(key[i], column);
            where = ReadPlanner.And(where, new BinaryExpr(SqlBinaryOperator.Equal, new ColumnExpr(names.Of(column)), new ParameterExpr(keyParameters[i])));
        }

        SqlExpr? filters = null;
        if (map.SoftDelete is { } soft) filters = new IsNullExpr(new ColumnExpr(names.Of(soft)));
        SqlParameterValue? tenantParameter = null;
        if (map.TenantKey is { } tenantColumn)
        {
            tenantParameter = SqlParameterValue.ForColumn(tenant, tenantColumn);
            filters = ReadPlanner.And(filters,
                new BinaryExpr(SqlBinaryOperator.Equal, new ColumnExpr(names.Of(tenantColumn)), new ParameterExpr(tenantParameter)));
        }

        var limit = SqlParameterValue.Of(1);
        var select = new SelectStatement(db.TableOf(map, names))
        {
            Projection = names.Projection(null),
            Where = ReadPlanner.And(where, filters),
            Limit = new ParameterExpr(limit),
        };

        RenderedSql rendered;
        try
        {
            rendered = SqlWriter.Render(select, db.Provider.Dialect);
        }
        catch (SqlRenderException)
        {
            return null;
        }

        var slots = new int[rendered.Parameters.Count];
        var constants = new SqlParameterValue?[rendered.Parameters.Count];
        for (var p = 0; p < slots.Length; p++)
        {
            var value = rendered.Parameters[p];
            var keyIndex = Array.FindIndex(keyParameters, k => ReferenceEquals(k, value));
            if (keyIndex >= 0) slots[p] = keyIndex;
            else if (ReferenceEquals(value, tenantParameter)) slots[p] = TenantSlot;
            else if (ReferenceEquals(value, limit))
            {
                slots[p] = ConstantSlot;
                constants[p] = limit;
            }
            else return null;
        }
        return new KeyLookupPlan(rendered.Text, slots, constants);
    }
}
