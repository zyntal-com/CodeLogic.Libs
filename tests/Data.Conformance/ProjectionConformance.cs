using CL.Data;
using CL.Data.Errors;
using CodeLogic.Core.Results;

namespace Data.Conformance;

/// <summary>Projection DTO used by the conformance suite.</summary>
public sealed class ProductSummary
{
    /// <summary>SKU.</summary>
    public string Sku { get; set; } = "";
    /// <summary>Stock value.</summary>
    public decimal Value { get; set; }
    /// <summary>Status.</summary>
    public ProductStatus Status { get; set; }
}

/// <summary>Projection record used by the conformance suite.</summary>
public sealed record ProductLine(long Id, string Name, bool InStock);

/// <summary>Projections every provider must support.</summary>
public abstract class ProjectionConformance(ProviderFixture fixture) : ConformanceSuite(fixture)
{
    private async Task SeedAsync() =>
        (await Db.Repository<Product>().InsertManyAsync([
            new Product { Sku = "A", Name = "alpha", Price = 2m, Stock = 3, Status = ProductStatus.Live },
            new Product { Sku = "B", Name = "bravo", Price = 5m, Stock = 0, Status = ProductStatus.Draft },
            new Product { Sku = "C", Name = "charlie", Price = 5m, Stock = 4, Status = ProductStatus.Live },
        ])).ValueOrThrow();

    [SkippableFact]
    public async Task Scalars_and_distinct()
    {
        RequireProvider();
        await SeedAsync();
        var q = Db.Query<Product>().OrderBy(p => p.Sku);
        Assert.Equal(["A", "B", "C"], (await q.Select(p => p.Sku).ToListAsync()).ValueOrThrow());
        Assert.Equal([2m, 5m], (await Db.Query<Product>().Select(p => p.Price).Distinct().ToListAsync()).ValueOrThrow().Order());
        Assert.Equal("ALPHA", (await q.Select(p => SqlFn.Upper(p.Name)).FirstOrDefaultAsync()).ValueOrThrow());
    }

    [SkippableFact]
    public async Task Anonymous_types_with_computed_values()
    {
        RequireProvider();
        await SeedAsync();
        var rows = (await Db.Query<Product>().OrderBy(p => p.Sku)
            .Select(p => new { p.Sku, Total = p.Price * p.Stock, Label = p.Name + "!", Big = p.Price > 3m }).ToListAsync()).ValueOrThrow();

        Assert.Equal(3, rows.Count);
        Assert.Equal(("A", 6m, "alpha!", false), (rows[0].Sku, rows[0].Total, rows[0].Label, rows[0].Big));
        Assert.Equal(("C", 20m, true), (rows[2].Sku, rows[2].Total, rows[2].Big));
    }

    [SkippableFact]
    public async Task Records_and_dtos_with_converted_columns()
    {
        RequireProvider();
        await SeedAsync();
        var lines = (await Db.Query<Product>().Where(p => p.Price == 5m).OrderBy(p => p.Sku)
            .Select(p => new ProductLine(p.Id, p.Name, p.Stock > 0)).ToListAsync()).ValueOrThrow();
        Assert.Equal([("bravo", false), ("charlie", true)], lines.Select(l => (l.Name, l.InStock)));

        var summaries = (await Db.Query<Product>().OrderBy(p => p.Sku)
            .Select(p => new ProductSummary { Sku = p.Sku, Value = p.Price * p.Stock, Status = p.Status }).ToListAsync()).ValueOrThrow();
        Assert.Equal([ProductStatus.Live, ProductStatus.Draft, ProductStatus.Live], summaries.Select(s => s.Status));   // enum stored as text
        Assert.Equal(20m, summaries[2].Value);
    }

    [SkippableFact]
    public async Task Conditional_values()
    {
        RequireProvider();
        await SeedAsync();
        var labels = (await Db.Query<Product>().OrderBy(p => p.Sku).Select(p => p.Stock > 0 ? "yes" : "no").ToListAsync()).ValueOrThrow();
        Assert.Equal(["yes", "no", "yes"], labels);
    }

    [SkippableFact]
    public async Task Nested_objects_are_rejected()
    {
        RequireProvider();
        var result = await Db.Query<Product>().Select(p => new { p.Id, Inner = new { p.Sku } }).ToListAsync();
        Assert.Equal(DataErrors.TranslationFailedCode, result.Error!.Code);
    }
}
