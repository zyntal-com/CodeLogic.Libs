using CL.Data;
using CL.Data.Mapping;
using CL.Data.Sql;
using CL.Data.Writes;
using Xunit.Abstractions;

namespace Data.Tests.Writes;

[Table(Name = "probe_items")]
public sealed class ProbeItem
{
    [Column(Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Size = 40)] public string Sku { get; set; } = "";
    [Column(Size = 100)] public string Name { get; set; } = "";
    [Column(Precision = 18, Scale = 2)] public decimal Price { get; set; }
    public int Stock { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Allocation budget for planning and rendering a 100-row insert (regression guard for the write path).</summary>
public sealed class InsertAllocationBudget(ITestOutputHelper output)
{
    [Fact]
    public void Planning_and_rendering_100_rows_stays_within_budget()
    {
        var accessor = EntityAccessorRegistry.Get<ProbeItem>().ValueOrThrow();
        var ctx = new WriteContext { Dialect = new Data.Tests.Providers.FakeDialect(), Capabilities = new() { ParameterLimit = 65535 } };
        List<ProbeItem> Rows() => Enumerable.Range(0, 100).Select(i => new ProbeItem { Sku = "S" + i, Name = "N" + i, Price = i, Stock = i, CreatedAt = DateTime.UtcNow }).ToList();
        for (var w = 0; w < 50; w++) { var p = WritePlanner.Insert(accessor, Rows(), ctx).ValueOrThrow(); SqlWriter.Render(p[0].Statement, ctx.Dialect); }

        var rows = Rows();
        var a = GC.GetAllocatedBytesForCurrentThread();
        var plan = WritePlanner.Insert(accessor, rows, ctx).ValueOrThrow();
        var b = GC.GetAllocatedBytesForCurrentThread();
        var rendered = SqlWriter.Render(plan[0].Statement, ctx.Dialect);
        var c = GC.GetAllocatedBytesForCurrentThread();
        output.WriteLine($"plan {b - a} B, render {c - b} B, sql {rendered.Text.Length} chars, params {rendered.Parameters.Count}");
        Assert.Equal(500, rendered.Parameters.Count);
        Assert.True(c - a < 64 * 1024, $"planning + rendering allocated {c - a} bytes (budget 64 KB; was 114 KB before WP-A1)");
    }
}
