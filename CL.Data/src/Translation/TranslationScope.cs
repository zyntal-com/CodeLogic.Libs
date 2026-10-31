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

    /// <summary>Lambda parameters in scope.</summary>
    public IReadOnlyCollection<ParameterExpression> Parameters => _tables.Keys;

    /// <summary>Binds <paramref name="parameter"/> to <paramref name="map"/> with <paramref name="alias"/>.</summary>
    public TranslationScope Bind(ParameterExpression parameter, EntityMap map, string? alias = null)
    {
        _tables[parameter] = new ScopeTable(map, alias);
        return this;
    }

    /// <summary>The table bound to <paramref name="parameter"/>, if any.</summary>
    public ScopeTable? TableOf(ParameterExpression parameter) => _tables.GetValueOrDefault(parameter);
}

/// <summary>Raised inside the translator; turned into <c>data.translation_failed</c> at the boundary.</summary>
internal sealed class TranslationException(string message, string? details = null) : Exception(message)
{
    public string? Details { get; } = details;
}
