using System.Linq.Expressions;

namespace CL.Data.Query;

/// <summary>
/// A standing condition applied to every read of the entities it targets (queries, joins, subqueries, key lookups,
/// set-based updates and deletes), like soft delete and tenant filters. Register filters in
/// <c>DatabaseOptions.Filters</c>; <c>QueryBuilder.IgnoreFilters()</c> lifts them for one query. Entity writes by key
/// are not filtered.
/// </summary>
public sealed class DataFilter
{
    private DataFilter(Type target, LambdaExpression predicate, string name)
    {
        Target = target;
        Predicate = predicate;
        Name = name;
    }

    /// <summary>
    /// A filter for <typeparamref name="T"/>: an entity type, or a base class or interface its properties come from
    /// (<c>DataFilter.For&lt;IArchivable&gt;(e =&gt; !e.Archived)</c> applies to every mapped type implementing it).
    /// </summary>
    public static DataFilter For<T>(Expression<Func<T, bool>> predicate, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return new DataFilter(typeof(T), predicate, name ?? typeof(T).Name);
    }

    /// <summary>The type (or base type / interface) the filter targets.</summary>
    public Type Target { get; }

    /// <summary>The condition.</summary>
    public LambdaExpression Predicate { get; }

    /// <summary>Name used by <c>IgnoreFilters(name)</c>.</summary>
    public string Name { get; }

    /// <summary>True when the filter applies to <paramref name="entity"/>.</summary>
    public bool AppliesTo(Type entity) => Target.IsAssignableFrom(entity);
}

/// <summary>Which user filters a read leaves out.</summary>
/// <param name="IgnoreAll">Leave out every user filter.</param>
/// <param name="Names">Leave out these filters (by name).</param>
internal readonly record struct FilterSelection(bool IgnoreAll, IReadOnlyCollection<string>? Names)
{
    public static FilterSelection None => default;
    public static FilterSelection All => new(true, null);

    public bool Ignores(string name) => IgnoreAll || (Names?.Contains(name, StringComparer.Ordinal) ?? false);
}
