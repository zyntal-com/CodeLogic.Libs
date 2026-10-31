using System.Numerics;

namespace CL.Data;

/// <summary>
/// SQL functions for use inside query lambdas (<c>Where</c>, <c>Select</c>, <c>GroupBy</c>, <c>OrderBy</c>, update
/// setters). Each provider renders its own SQL. Calling them outside a query throws.
/// </summary>
/// <example><c>db.Query&lt;Order&gt;().Where(o =&gt; SqlFn.Year(o.CreatedAt) == 2026 &amp;&amp; SqlFn.Lower(o.Email) == email)</c></example>
public static class SqlFn
{
    /// <summary>Year of a date.</summary>
    public static int Year(DateTimeOffset value) => throw OutsideQuery();
    /// <summary>Year of a date.</summary>
    public static int Year(DateTime value) => throw OutsideQuery();
    /// <summary>Month (1-12).</summary>
    public static int Month(DateTimeOffset value) => throw OutsideQuery();
    /// <summary>Month (1-12).</summary>
    public static int Month(DateTime value) => throw OutsideQuery();
    /// <summary>Day of month.</summary>
    public static int Day(DateTimeOffset value) => throw OutsideQuery();
    /// <summary>Day of month.</summary>
    public static int Day(DateTime value) => throw OutsideQuery();
    /// <summary>Hour (0-23).</summary>
    public static int Hour(DateTimeOffset value) => throw OutsideQuery();
    /// <summary>Hour (0-23).</summary>
    public static int Hour(DateTime value) => throw OutsideQuery();
    /// <summary>Minute.</summary>
    public static int Minute(DateTimeOffset value) => throw OutsideQuery();
    /// <summary>Minute.</summary>
    public static int Minute(DateTime value) => throw OutsideQuery();
    /// <summary>Day of week, 0 = Sunday.</summary>
    public static int DayOfWeek(DateTimeOffset value) => throw OutsideQuery();
    /// <summary>Day of week, 0 = Sunday.</summary>
    public static int DayOfWeek(DateTime value) => throw OutsideQuery();
    /// <summary>Date part (midnight UTC).</summary>
    public static DateTimeOffset Date(DateTimeOffset value) => throw OutsideQuery();
    /// <summary>Date part (midnight).</summary>
    public static DateTime Date(DateTime value) => throw OutsideQuery();
    /// <summary>Start of the UTC bucket of <paramref name="bucketSeconds"/> seconds that contains <paramref name="value"/> (time-series grouping).</summary>
    public static DateTimeOffset BucketUtc(DateTimeOffset value, int bucketSeconds) => throw OutsideQuery();
    /// <summary>Start of the UTC bucket of <paramref name="bucketSeconds"/> seconds that contains <paramref name="value"/>.</summary>
    public static DateTime BucketUtc(DateTime value, int bucketSeconds) => throw OutsideQuery();
    /// <summary>Current UTC time on the database server.</summary>
    public static DateTimeOffset UtcNow() => throw OutsideQuery();

    /// <summary>First non-null value.</summary>
    public static T Coalesce<T>(params T[] values) => throw OutsideQuery();
    /// <summary><paramref name="value"/>, or <paramref name="fallback"/> when it is null.</summary>
    public static T IfNull<T>(T value, T fallback) => throw OutsideQuery();
    /// <summary>Smallest value.</summary>
    public static T Least<T>(params T[] values) => throw OutsideQuery();
    /// <summary>Largest value.</summary>
    public static T Greatest<T>(params T[] values) => throw OutsideQuery();

    /// <summary>Lower case.</summary>
    public static string Lower(string value) => throw OutsideQuery();
    /// <summary>Upper case.</summary>
    public static string Upper(string value) => throw OutsideQuery();
    /// <summary>Trimmed.</summary>
    public static string Trim(string value) => throw OutsideQuery();
    /// <summary>Character length.</summary>
    public static int Length(string value) => throw OutsideQuery();
    /// <summary>Concatenation.</summary>
    public static string Concat(params string[] parts) => throw OutsideQuery();
    /// <summary>
    /// SQL <c>LIKE</c> with your own pattern (<c>%</c> and <c>_</c> are wildcards, escaped with <c>\</c>). Prefer
    /// <c>string.Contains</c>/<c>StartsWith</c>, which escape for you.
    /// </summary>
    public static bool Like(string value, string pattern) => throw OutsideQuery();

    /// <summary>Rounded to <paramref name="digits"/> decimals.</summary>
    public static T Round<T>(T value, int digits) where T : INumber<T> => throw OutsideQuery();
    /// <summary>Rounded down.</summary>
    public static T Floor<T>(T value) where T : INumber<T> => throw OutsideQuery();
    /// <summary>Rounded up.</summary>
    public static T Ceiling<T>(T value) where T : INumber<T> => throw OutsideQuery();
    /// <summary>Absolute value.</summary>
    public static T Abs<T>(T value) where T : INumber<T> => throw OutsideQuery();

    /// <summary>JSON functions on JSON columns (native JSON where available).</summary>
    public static class Json
    {
        /// <summary>The scalar at <paramref name="path"/> (<c>$.a.b</c>) as <typeparamref name="T"/>.</summary>
        public static T Value<T>(object? document, string path) => throw OutsideQuery();
        /// <summary>True when <paramref name="path"/> exists in the document.</summary>
        public static bool Exists(object? document, string path) => throw OutsideQuery();
        /// <summary>True when the document contains <paramref name="value"/> (a JSON fragment).</summary>
        public static bool Contains(object? document, string value) => throw OutsideQuery();
    }

    /// <summary>Full-text search (needs a full-text index on the column where the provider requires one).</summary>
    public static class FullText
    {
        /// <summary>True when <paramref name="column"/> matches <paramref name="terms"/> in the provider's full-text syntax.</summary>
        public static bool Match(string column, string terms) => throw OutsideQuery();
    }

    private static InvalidOperationException OutsideQuery() =>
        new("SqlFn functions can only be used inside CL.Data query expressions.");
}
