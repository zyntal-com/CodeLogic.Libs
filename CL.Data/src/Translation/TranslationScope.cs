using System.Linq.Expressions;
using CL.Data.Mapping;
using CL.Data.Sql;

namespace CL.Data.Translation;

/// <summary>A lambda parameter bound to a mapped table and its alias in the statement.</summary>
/// <param name="Map">Mapping of the parameter's type.</param>
/// <param name="Alias">Table alias in the statement (null for single-table statements).</param>
public sealed record ScopeTable(EntityMap Map, string? Alias);

/// <summary>Context for translating lambdas: dialect, naming policy and the tables the lambda parameters stand for.</summary>
public sealed class TranslationScope(SqlDialect dialect, NamingPolicy policy)
{
    private readonly Dictionary<ParameterExpression, ScopeTable> _tables = [];

    /// <summary>The dialect (used for LIKE escaping).</summary>
    public SqlDialect Dialect { get; } = dialect;

    /// <summary>Naming policy for column names.</summary>
    public NamingPolicy Policy { get; } = policy;

    /// <summary>Lambda parameters in scope (tables, plus the group parameter of a grouped query).</summary>
    public IReadOnlyCollection<ParameterExpression> Parameters =>
        Group is null ? _tables.Keys : [.. _tables.Keys, Group.Parameter];

    /// <summary>The group a grouped-query lambda works on, when translating <c>Having</c>/<c>Select</c>/<c>OrderBy</c> of a group.</summary>
    public GroupScope? Group { get; init; }

    /// <summary>Binds <paramref name="parameter"/> to <paramref name="map"/> with <paramref name="alias"/>.</summary>
    public TranslationScope Bind(ParameterExpression parameter, EntityMap map, string? alias = null)
    {
        _tables[parameter] = new ScopeTable(map, alias);
        return this;
    }

    /// <summary>The table bound to <paramref name="parameter"/>, if any.</summary>
    public ScopeTable? TableOf(ParameterExpression parameter) => _tables.GetValueOrDefault(parameter);
}

/// <summary>The group parameter of a grouped-query lambda and what its key translates to.</summary>
/// <param name="Parameter">The <c>IDataGroup</c> lambda parameter.</param>
/// <param name="Key">SQL of a single-value key; null for a composite key.</param>
/// <param name="KeyMembers">SQL of each member of a composite (anonymous type) key.</param>
/// <param name="Table">The grouped table (aggregate selectors bind to it).</param>
public sealed record GroupScope(ParameterExpression Parameter, SqlExpr? Key, IReadOnlyDictionary<string, SqlExpr> KeyMembers, ScopeTable Table);

/// <summary>Raised inside the translator; turned into <c>data.translation_failed</c> at the boundary.</summary>
internal sealed class TranslationException(string message, string? details = null) : Exception(message)
{
    public string? Details { get; } = details;
}
