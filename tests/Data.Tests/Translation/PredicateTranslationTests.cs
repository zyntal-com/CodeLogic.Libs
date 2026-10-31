using System.Linq.Expressions;
using CL.Data;
using CL.Data.Errors;
using CL.Data.Mapping;
using CL.Data.Mapping.Conversion;
using CL.Data.Sql;
using CL.Data.Translation;
using CodeLogic.Core.Results;
using Data.Tests.Providers;

namespace Data.Tests.Translation;

public enum Tier { Free, Pro }

[Table(Name = "accounts")]
public sealed class Account
{
    [Column(Primary = true)] public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? Nick { get; set; }
    public bool Active { get; set; }
    public decimal Score { get; set; }
    public int? ParentId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    [Column(Converter = typeof(EnumToStringConverter<Tier>))] public Tier Tier { get; set; }
    public Tier PlainTier { get; set; }
}

public sealed class PredicateTranslationTests
{
    private static readonly SqlDialect Dialect = new FakeDialect();

    private static (string Sql, object?[] Values) Where(Expression<Func<Account, bool>> predicate)
    {
        var map = EntityAccessorRegistry.Get<Account>().ValueOrThrow().Map;
        var scope = new TranslationScope(Dialect, NamingPolicy.AsIs).Bind(predicate.Parameters[0], map);
        var expr = SqlTranslator.TranslatePredicate(predicate, scope).ValueOrThrow();
        var rendered = SqlWriter.RenderExpression(expr, Dialect);
        return (rendered.Text, rendered.Parameters.Select(p => p.BoxedValue).ToArray());
    }

    private static Error Fails(Expression<Func<Account, bool>> predicate)
    {
        var map = EntityAccessorRegistry.Get<Account>().ValueOrThrow().Map;
        var scope = new TranslationScope(Dialect, NamingPolicy.AsIs).Bind(predicate.Parameters[0], map);
        var result = SqlTranslator.TranslatePredicate(predicate, scope);
        Assert.True(result.IsFailure);
        return result.Error!;
    }

    [Fact]
    public void Captured_values_become_parameters_and_code_numbers_literals()
    {
        var name = "Ann";
        var (sql, values) = Where(a => a.Name == name && a.Score > 10);
        Assert.Equal("\"Name\" = @p0 AND \"Score\" > 10", sql);
        Assert.Equal(["Ann"], values);
    }

    [Fact]
    public void Null_comparisons_become_is_null_even_for_captured_nulls()
    {
        string? nick = null;
        Assert.Equal("\"Nick\" IS NULL", Where(a => a.Nick == null).Sql);
        Assert.Equal("\"Nick\" IS NOT NULL", Where(a => null != a.Nick).Sql);
        Assert.Equal("\"Nick\" IS NULL", Where(a => a.Nick == nick).Sql);
        Assert.Equal("\"ParentId\" IS NOT NULL", Where(a => a.ParentId.HasValue).Sql);
        Assert.Equal("\"ParentId\" > 3", Where(a => a.ParentId!.Value > 3).Sql);
    }

    [Fact]
    public void Boolean_columns_in_conditions()
    {
        Assert.Equal("\"Active\" = TRUE", Where(a => a.Active).Sql);
        Assert.Equal("NOT (\"Active\" = TRUE)", Where(a => !a.Active).Sql);
        Assert.Equal("\"Active\" = TRUE OR \"Score\" < 0", Where(a => a.Active || a.Score < 0).Sql);
        var flag = false;
        Assert.Equal("\"Active\" = @p0", Where(a => a.Active == flag).Sql);
    }

    [Fact]
    public void String_matching_escapes_wildcards()
    {
        var term = "50%_off";
        var (sql, values) = Where(a => a.Name.Contains(term));
        Assert.Equal("\"Name\" LIKE @p0 ESCAPE '\\'", sql);
        Assert.Equal(["%50\\%\\_off%"], values);
        Assert.Equal(["abc%"], Where(a => a.Name.StartsWith("abc")).Values);
        Assert.Equal(["%abc"], Where(a => a.Name.EndsWith("abc")).Values);
        string? none = null;
        Assert.Equal("FALSE", Where(a => a.Name.Contains(none!)).Sql);
        Assert.Equal("\"Name\" LIKE (@p0) || (\"Nick\") ESCAPE '\\'", Where(a => a.Name.EndsWith(a.Nick!)).Sql);
    }

    [Fact]
    public void Collections_become_in_lists_including_arrays_bound_to_spans()
    {
        long[] ids = [1, 2, 3];                       // C# 14+: binds MemoryExtensions.Contains(ReadOnlySpan<long>, long)
        var (sql, values) = Where(a => ids.Contains(a.Id));
        Assert.Equal("\"Id\" IN (@p0, @p1, @p2)", sql);
        Assert.Equal([1L, 2L, 3L], values);

        List<string> names = ["a", "b"];
        Assert.Equal("\"Name\" NOT IN (@p0, @p1)", Where(a => !names.Contains(a.Name)).Sql);
        Assert.Equal("1 = 0", Where(a => Array.Empty<long>().Contains(a.Id)).Sql);

        int?[] parents = [7, null];
        Assert.Equal("\"ParentId\" IN (@p0) OR \"ParentId\" IS NULL", Where(a => parents.Contains(a.ParentId)).Sql);
    }

    [Fact]
    public void Long_in_lists_are_split()
    {
        var map = EntityAccessorRegistry.Get<Account>().ValueOrThrow().Map;
        var ids = Enumerable.Range(0, 5).Select(i => (long)i).ToArray();
        Expression<Func<Account, bool>> predicate = a => ids.Contains(a.Id);
        var scope = new TranslationScope(Dialect, NamingPolicy.AsIs).Bind(predicate.Parameters[0], map);
        var translator = new ExpressionTranslator(scope) { MaxInListValues = 2 };
        var sql = SqlWriter.RenderExpression(translator.Predicate(predicate.Body), Dialect).Text;
        Assert.Equal("\"Id\" IN (@p0, @p1) OR \"Id\" IN (@p2, @p3) OR \"Id\" IN (@p4)", sql);
    }

    [Fact]
    public void Column_converters_apply_to_compared_values()
    {
        Assert.Equal(["Pro"], Where(a => a.Tier == Tier.Pro).Values);              // enum stored as text
        Assert.Equal("\"PlainTier\" = 1", Where(a => a.PlainTier == Tier.Pro).Sql);  // plain enum: its number, inline
        Tier[] tiers = [Tier.Free, Tier.Pro];
        Assert.Equal(["Free", "Pro"], Where(a => tiers.Contains(a.Tier)).Values);
    }

    [Fact]
    public void Arithmetic_functions_and_conditionals()
    {
        Assert.Equal("\"Score\" * 2 + 1 > 10", Where(a => a.Score * 2 + 1 > 10).Sql);
        Assert.Equal("EXTRACT(YEAR FROM \"CreatedAt\") = 2026", Where(a => a.CreatedAt.Year == 2026).Sql);
        Assert.Equal("CHAR_LENGTH(\"Name\") > 3", Where(a => a.Name.Length > 3).Sql);
        Assert.Equal("LOWER(\"Name\") = @p0", Where(a => a.Name.ToLower() == "ann").Sql);
        Assert.Equal("COALESCE(\"Nick\", \"Name\") = @p0", Where(a => (a.Nick ?? a.Name) == "x").Sql);
        Assert.Equal("ABS(\"Score\") < 5", Where(a => Math.Abs(a.Score) < 5).Sql);
        Assert.Equal("SUBSTRING(\"Name\", 0 + 1, 2) = @p0", Where(a => a.Name.Substring(0, 2) == "an").Sql);
        Assert.Equal("CASE WHEN \"Active\" = TRUE THEN \"Score\" ELSE 0 END > 1", Where(a => (a.Active ? a.Score : 0) > 1).Sql);
        Assert.Equal("\"Name\" IS NULL OR \"Name\" = @p0", Where(a => string.IsNullOrEmpty(a.Name)).Sql);
    }

    [Fact]
    public void Client_only_parts_are_evaluated_once()
    {
        var before = DateTimeOffset.UtcNow;
        var (sql, values) = Where(a => a.CreatedAt > DateTimeOffset.UtcNow.AddDays(-7));
        Assert.Equal("\"CreatedAt\" > @p0", sql);
        var cutoff = Assert.IsType<DateTimeOffset>(values[0]);
        Assert.InRange(cutoff, before.AddDays(-7).AddSeconds(-5), DateTimeOffset.UtcNow.AddDays(-7).AddSeconds(5));
    }

    [Fact]
    public void Untranslatable_calls_fail_instead_of_running_on_the_client()
    {
        var error = Fails(a => a.Name.GetHashCode() == 5);
        Assert.Equal(DataErrors.TranslationFailedCode, error.Code);
        Assert.Contains("GetHashCode", error.Message);

        Account? missing = null;
        var thrown = Fails(a => a.Name == missing!.Name);
        Assert.Equal(DataErrors.TranslationFailedCode, thrown.Code);
    }

    [Fact]
    public void Values_never_reach_the_sql_text()
    {
        var secret = "' OR 1=1 --";
        var big = 123456789012L;
        var (sql, values) = Where(a => a.Name == secret || a.Id == big || a.Name.Contains(secret));
        Assert.DoesNotContain(secret, sql);
        Assert.DoesNotContain("123456789012", sql);
        Assert.Contains(secret, values);
    }
}
