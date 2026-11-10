using CL.Data.Configuration;
using CL.Data.Errors;
using CL.Data.Execution;
using CL.Data.Pipeline;
using CL.Data.Providers;
using CL.Data.Sqlite;
using CodeLogic.Core.Results;

namespace Data.Sqlite.Tests;

public sealed class PipelineTests
{
    private sealed class Recording(string name, List<string> log) : ICommandMiddleware
    {
        public async ValueTask<Result<T>> InvokeAsync<T>(CommandContext context, CommandNext<T> next)
        {
            log.Add($"{name}:before");
            var result = await next.InvokeAsync(context);
            log.Add($"{name}:after rows={context.Rows}");
            return result;
        }
    }

    private sealed class ShortCircuit : ICommandMiddleware
    {
        public ValueTask<Result<T>> InvokeAsync<T>(CommandContext context, CommandNext<T> next) =>
            ValueTask.FromResult<Result<T>>(DataErrors.CircuitOpen("primary"));
    }

    private sealed class Commenting(List<string> seen) : IDataInterceptor
    {
        public ValueTask<Error?> OnExecutingAsync(CommandContext context)
        {
            context.Spec = context.Spec with { Sql = "/* app */ " + context.Spec.Sql };
            return ValueTask.FromResult<Error?>(null);
        }

        public ValueTask OnExecutedAsync(CommandContext context, Error? error)
        {
            seen.Add($"{context.Spec.Sql}|{context.IsWrite}|{error?.Code}");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Blocking : IDataInterceptor
    {
        public ValueTask<Error?> OnExecutingAsync(CommandContext context) =>
            ValueTask.FromResult<Error?>(context.IsWrite ? DataErrors.AccessDenied("read-only window") : null);
    }

    /// <summary>Swaps two columns in the SELECT list of reads, as an interceptor that rewrites SQL may.</summary>
    private sealed class Reordering : IDataInterceptor
    {
        public int Rewritten;

        public ValueTask<Error?> OnExecutingAsync(CommandContext context)
        {
            const string original = "\"Sku\", \"Name\"", swapped = "\"Name\", \"Sku\"";
            if (!context.IsWrite && context.Spec.Sql.Contains(original, StringComparison.Ordinal))
            {
                context.Spec = context.Spec with { Sql = context.Spec.Sql.Replace(original, swapped, StringComparison.Ordinal) };
                Rewritten++;
            }
            return ValueTask.FromResult<Error?>(null);
        }
    }

    private static readonly CL.Data.Query.CompiledQuery<Data.Conformance.Product, long> ProductById =
        CL.Data.Query.CompiledQuery.For<Data.Conformance.Product, long>((q, id) => q.Where(p => p.Id == id));

    [Fact]
    public async Task Rows_are_matched_by_name_when_an_interceptor_changes_the_select_list()
    {
        // CL.Data reads the rows of its own whole-entity SELECTs by position; a rewritten statement must fall back to names.
        var reordering = new Reordering();
        await using var db = Create(new DatabaseOptions { Interceptors = [reordering] });
        (await db.Schema.SyncAsync<Data.Conformance.Product>()).ValueOrThrow();
        var row = (await db.Repository<Data.Conformance.Product>().InsertAsync(
            new Data.Conformance.Product { Sku = "S-1", Name = "first", Price = 1, Stock = 1 })).ValueOrThrow();

        var byKey = (await db.Repository<Data.Conformance.Product>().GetByIdAsync(row.Id)).ValueOrThrow()!;
        var listed = (await db.Query<Data.Conformance.Product>().Where(p => p.Stock == 1).ToListAsync()).ValueOrThrow().Single();
        var compiled = (await ProductById.FirstOrDefaultAsync(db, row.Id)).ValueOrThrow()!;

        Assert.Equal(3, reordering.Rewritten);
        Assert.Equal(("S-1", "first"), (byKey.Sku, byKey.Name));
        Assert.Equal(("S-1", "first"), (listed.Sku, listed.Name));
        Assert.Equal(("S-1", "first"), (compiled.Sku, compiled.Name));
    }

    private static Database Create(DatabaseOptions options)
    {
        DataProviderRegistry.Register(SqliteDataProvider.Instance);
        return Database.Create("p", new DataConnectionConfig { Provider = "sqlite", Database = ":memory:" }, options).ValueOrThrow();
    }

    [Fact]
    public async Task Middleware_runs_outermost_first_and_sees_row_counts()
    {
        List<string> log = [];
        await using var db = Create(new DatabaseOptions { Middleware = [new Recording("a", log), new Recording("b", log)] });

        var values = (await db.SqlQueryAsync<long>($"SELECT 1 UNION ALL SELECT 2")).ValueOrThrow();

        Assert.Equal([1L, 2L], values);
        Assert.Equal(["a:before", "b:before", "b:after rows=2", "a:after rows=2"], log);
    }

    [Fact]
    public async Task Middleware_can_answer_without_running_the_command()
    {
        await using var db = Create(new DatabaseOptions { Middleware = [new ShortCircuit()] });
        var result = await db.SqlScalarAsync<long>($"SELECT 1");
        Assert.Equal(DataErrors.CircuitOpenCode, result.Error!.Code);
    }

    [Fact]
    public async Task Interceptors_rewrite_sql_and_observe_results()
    {
        List<string> seen = [];
        await using var db = Create(new DatabaseOptions { Interceptors = [new Commenting(seen)] });

        Assert.Equal(7L, (await db.SqlScalarAsync<long>($"SELECT {7L}")).ValueOrThrow());
        (await db.ExecuteSqlAsync($"BROKEN")).ToResult();

        Assert.Equal("/* app */ SELECT @p0|False|", seen[0]);
        Assert.Equal($"/* app */ BROKEN|True|{DataErrors.ProviderErrorCode}", seen[1]);
    }

    [Fact]
    public async Task Interceptor_can_refuse_a_command()
    {
        await using var db = Create(new DatabaseOptions { Interceptors = [new Blocking()] });
        Assert.True((await db.SqlScalarAsync<long>($"SELECT 1")).IsSuccess);
        var write = await db.ExecuteSqlAsync($"CREATE TABLE t (x INTEGER)");
        Assert.Equal(DataErrors.AccessDeniedCode, write.Error!.Code);
    }
}
