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
