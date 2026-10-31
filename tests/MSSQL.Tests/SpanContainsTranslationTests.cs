using CL.MSSQL.Core;
using Xunit;

namespace MSSQL.Tests;

/// <summary>
/// Under C# 14+ ("first-class spans") <c>array.Contains(x)</c> in an expression tree binds to
/// <c>MemoryExtensions.Contains(ReadOnlySpan&lt;T&gt;, T)</c>, with the array wrapped in an implicit span
/// conversion. Evaluating that conversion through reflection throws, so the translator must read the
/// array itself. These run offline: they only translate, they don't query.
/// </summary>
public sealed class SpanContainsTranslationTests
{
    public sealed class Row
    {
        public long Id { get; set; }
    }

    [Fact]
    public void Array_Contains_becomes_an_IN_clause()
    {
        var ids = new long[] { 1, 2, 3 };

        var (sql, parameters) = SqlServerExpressionVisitor.Translate<Row>(r => ids.Contains(r.Id));

        Assert.Contains(" IN (", sql);
        Assert.Equal(3, parameters.Count);
    }

    [Fact]
    public void Empty_array_Contains_matches_nothing()
    {
        var ids = Array.Empty<long>();

        var (sql, parameters) = SqlServerExpressionVisitor.Translate<Row>(r => ids.Contains(r.Id));

        Assert.Contains("1 = 0", sql);
        Assert.Empty(parameters);
    }
}
