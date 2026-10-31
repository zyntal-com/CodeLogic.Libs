using System.Numerics;

namespace CL.Data.Query;

/// <summary>
/// A group in a grouped query: its key and aggregates over its rows. Used only inside query lambdas
/// (<c>Having</c>, <c>OrderBy</c>, <c>Select</c>), where each member translates to SQL; calling it directly throws.
/// </summary>
/// <typeparam name="TKey">Group key (a value, or an anonymous type for several columns).</typeparam>
/// <typeparam name="TElement">Grouped entity.</typeparam>
public interface IDataGroup<out TKey, TElement>
{
    /// <summary>The group key. For a composite key, use its members (<c>g.Key.Year</c>).</summary>
    TKey Key { get; }

    /// <summary>Rows in the group.</summary>
    int Count();

    /// <summary>Rows in the group matching <paramref name="predicate"/>.</summary>
    int Count(Func<TElement, bool> predicate);

    /// <summary>Rows in the group, as a 64-bit count.</summary>
    long LongCount();

    /// <summary>Distinct non-null values of <paramref name="selector"/>.</summary>
    int CountDistinct<TValue>(Func<TElement, TValue> selector);

    /// <summary>Sum of <paramref name="selector"/> (NULLs count as zero).</summary>
    TValue Sum<TValue>(Func<TElement, TValue> selector) where TValue : struct, INumber<TValue>;

    /// <summary>Average of <paramref name="selector"/> as a double (integer columns are not truncated).</summary>
    double Average<TValue>(Func<TElement, TValue> selector) where TValue : struct, INumber<TValue>;

    /// <summary>Average of a decimal <paramref name="selector"/>.</summary>
    decimal Average(Func<TElement, decimal> selector);

    /// <summary>Smallest value of <paramref name="selector"/>.</summary>
    TValue Min<TValue>(Func<TElement, TValue> selector);

    /// <summary>Largest value of <paramref name="selector"/>.</summary>
    TValue Max<TValue>(Func<TElement, TValue> selector);

    /// <summary>True when the group has rows (always true; for symmetry with <see cref="Any(Func{TElement, bool})"/>).</summary>
    bool Any();

    /// <summary>True when any row of the group matches <paramref name="predicate"/>.</summary>
    bool Any(Func<TElement, bool> predicate);
}
