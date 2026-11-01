using System.Text;
using CL.Data.Execution;

namespace Data.Tests.Execution;

public sealed class CacheKeyTests
{
    private static string Key(object? value)
    {
        var sb = new StringBuilder();
        Database.AppendKeyValue(sb, value, 0);
        return sb.ToString();
    }

    [Fact]
    public void Array_parameters_are_keyed_by_their_elements()
    {
        Assert.NotEqual(Key(new[] { 1, 2 }), Key(new[] { 1, 3 }));
        Assert.Equal(Key(new[] { 1, 2 }), Key(new[] { 1, 2 }));
        Assert.NotEqual(Key(new[] { "a", "b" }), Key(new[] { "a,b" }));
        Assert.NotEqual(Key(new List<long> { 5 }), Key(new List<long> { 6 }));
        Assert.NotEqual(Key(new int?[] { null }), Key(new int?[] { 0 }));
    }

    [Fact]
    public void Scalars_keep_distinct_keys()
    {
        Assert.NotEqual(Key("1"), Key(1));
        Assert.NotEqual(Key(1), Key(1L));
        Assert.NotEqual(Key(null), Key(""));
        Assert.NotEqual(Key(new byte[] { 1 }), Key(new byte[] { 2 }));
    }
}
