using System.Collections.Concurrent;
using CL.Data.PostgreSql;
using CodeLogic.Core.Results;
using Data.Conformance;

namespace Data.PostgreSql.Tests;

[Collection("postgresql")]
public sealed class PostgreSqlExtensionTests(PostgreSqlFixture fixture)
{
    [SkippableFact]
    public async Task Listen_receives_notifications_until_disposed()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        var db = fixture.Db;
        var channel = "cl_data_" + Guid.NewGuid().ToString("N")[..8];
        ConcurrentQueue<PostgresNotification> received = new();
        var listener = (await db.Postgres.ListenAsync(channel, n => { received.Enqueue(n); return ValueTask.CompletedTask; })).ValueOrThrow();
        await using (listener)
        {
            (await db.Postgres.NotifyAsync(channel, "hello")).ThrowIfFailure();
            (await db.Postgres.NotifyAsync(channel)).ThrowIfFailure();
            for (var i = 0; i < 100 && received.Count < 2; i++) await Task.Delay(50);
            Assert.Equal(["hello", ""], received.Select(n => n.Payload));
            Assert.All(received, n => Assert.Equal(channel, n.Channel));
        }
        (await db.Postgres.NotifyAsync(channel, "after")).ThrowIfFailure();
        await Task.Delay(200);
        Assert.Equal(2, received.Count);                                     // disposed: nothing more arrives
    }

    [SkippableFact]
    public async Task Vacuum_analyze_runs_outside_transactions_only()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (await fixture.Db.Postgres.VacuumAnalyzeAsync<Product>()).ThrowIfFailure();
        var tx = (await fixture.Db.BeginTransactionAsync()).ValueOrThrow();
        await using (tx) Assert.Equal(CL.Data.Errors.DataErrors.UnsupportedCode, (await tx.Db.Postgres.VacuumAnalyzeAsync<Product>()).Error!.Code);
    }
}
