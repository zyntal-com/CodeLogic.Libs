using CL.Data.Errors;
using CodeLogic.Core.Results;

namespace Data.Conformance;

/// <summary>Offset paging, keyset (cursor) paging and streaming.</summary>
public abstract class PagingConformance(ProviderFixture fixture) : ConformanceSuite(fixture)
{
    private async Task<List<Product>> SeedAsync(int count)
    {
        // Prices repeat so the key has to break ties.
        var products = Enumerable.Range(0, count)
            .Select(i => new Product { Sku = $"S{i:000}", Name = $"N{i}", Price = i % 3, Stock = i })
            .ToList();
        (await Db.Repository<Product>().InsertManyAsync(products)).ValueOrThrow();
        return products;
    }

    [SkippableFact]
    public async Task Offset_pages()
    {
        RequireProvider();
        await SeedAsync(10);
        var q = Db.Query<Product>().OrderBy(p => p.Sku);

        var page2 = (await q.ToPagedListAsync(2, 4, withTotal: true)).ValueOrThrow();
        Assert.Equal(["S004", "S005", "S006", "S007"], page2.Items.Select(p => p.Sku));
        Assert.True(page2.HasMore);
        Assert.Equal(10L, page2.TotalCount);

        var page3 = (await q.ToPagedListAsync(3, 4)).ValueOrThrow();
        Assert.Equal(2, page3.Items.Count);
        Assert.False(page3.HasMore);
        Assert.Null(page3.TotalCount);
    }

    [SkippableFact]
    public async Task Cursor_pages_cover_every_row_once_with_ties()
    {
        RequireProvider();
        await SeedAsync(23);
        var q = Db.Query<Product>().Where(p => p.Stock >= 0).OrderByDescending(p => p.Price);

        List<string> seen = [];
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = (await q.ToCursorPageAsync(5, cursor)).ValueOrThrow();
            seen.AddRange(page.Items.Select(p => p.Sku));
            cursor = page.NextCursor;
            pages++;
        } while (cursor is not null);

        Assert.Equal(5, pages);
        Assert.Equal(23, seen.Count);
        Assert.Equal(23, seen.Distinct().Count());
        var all = (await q.ToListAsync()).ValueOrThrow();
        Assert.Equal(all.OrderByDescending(p => p.Price).ThenByDescending(p => p.Id).Select(p => p.Sku), seen);
    }

    [SkippableFact]
    public async Task Cursor_survives_inserts_between_pages()
    {
        RequireProvider();
        await SeedAsync(6);
        var q = Db.Query<Product>().OrderBy(p => p.Sku);
        var first = (await q.ToCursorPageAsync(3)).ValueOrThrow();
        Assert.Equal(["S000", "S001", "S002"], first.Items.Select(p => p.Sku));

        await Db.Repository<Product>().InsertAsync(new Product { Sku = "S000a", Name = "late" });   // sorts before the cursor
        var second = (await q.ToCursorPageAsync(3, first.NextCursor)).ValueOrThrow();
        Assert.Equal(["S003", "S004", "S005"], second.Items.Select(p => p.Sku));
    }

    [SkippableFact]
    public async Task Foreign_or_broken_cursors_are_rejected()
    {
        RequireProvider();
        await SeedAsync(4);
        var cursor = (await Db.Query<Product>().OrderBy(p => p.Sku).ToCursorPageAsync(2)).ValueOrThrow().NextCursor;

        var otherOrder = await Db.Query<Product>().OrderBy(p => p.Name).ToCursorPageAsync(2, cursor);
        Assert.Equal(DataErrors.InvalidCursorCode, otherOrder.Error!.Code);
        var garbage = await Db.Query<Product>().OrderBy(p => p.Sku).ToCursorPageAsync(2, "not-a-cursor!");
        Assert.Equal(DataErrors.InvalidCursorCode, garbage.Error!.Code);
    }

    [SkippableFact]
    public async Task Streaming_yields_every_row()
    {
        RequireProvider();
        await SeedAsync(50);
        var count = 0;
        long stock = 0;
        await foreach (var p in Db.Query<Product>().Where(p => p.Stock >= 10).AsAsyncEnumerable())
        {
            count++;
            stock += p.Stock;
        }
        Assert.Equal(40, count);
        Assert.Equal(Enumerable.Range(10, 40).Sum(), stock);
    }

    [SkippableFact]
    public async Task Streaming_failures_throw_data_query_exceptions()
    {
        RequireProvider();
        var ex = await Assert.ThrowsAsync<DataQueryException>(async () =>
        {
            await foreach (var _ in Db.Query<Product>().Where(p => p.Name.GetHashCode() == 0).AsAsyncEnumerable()) { }
        });
        Assert.Equal(DataErrors.TranslationFailedCode, ex.Error.Code);
    }
}
