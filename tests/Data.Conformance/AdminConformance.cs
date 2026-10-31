using CL.Data;
using CL.Data.Events;
using CL.Data.Execution;
using CodeLogic.Core.Results;

namespace Data.Conformance;

/// <summary>Scratch table for admin tests.</summary>
[Table(Name = "adm_items")]
public sealed class AdminItem
{
    /// <summary>Identity.</summary>
    [Column(Primary = true, AutoIncrement = true)] public long Id { get; set; }
    /// <summary>Name.</summary>
    [Column(Size = 50)] public string Name { get; set; } = "";
}

/// <summary>Rows that expire after seven days, purged two at a time.</summary>
[Table(Name = "ret_logs")]
public sealed class RetentionLog
{
    /// <summary>Identity.</summary>
    [Column(Primary = true, AutoIncrement = true)] public long Id { get; set; }
    /// <summary>When the row was written.</summary>
    [RetainDays(7, BatchSize = 2)] public DateTimeOffset At { get; set; }
    /// <summary>Message.</summary>
    [Column(Size = 50)] public string Message { get; set; } = "";
}

/// <summary>Tenant-scoped rows that expire after seven days.</summary>
[Table(Name = "ret_tenant_logs")]
public sealed class TenantRetentionLog
{
    /// <summary>Identity.</summary>
    [Column(Primary = true, AutoIncrement = true)] public long Id { get; set; }
    /// <summary>Tenant.</summary>
    [TenantKey] public int TenantId { get; set; }
    /// <summary>When the row was written.</summary>
    [RetainDays(7)] public DateTimeOffset At { get; set; }
}

/// <summary>Database and table administration, and retention purges.</summary>
public abstract class AdminConformance(ProviderFixture fixture) : ConformanceSuite(fixture)
{
    [SkippableFact]
    public async Task Databases_can_be_created_checked_and_dropped()
    {
        RequireProvider();
        var admin = Db.Admin;
        Assert.False(string.IsNullOrEmpty(admin.DatabaseName.ValueOrThrow()));
        Assert.True((await admin.DatabaseExistsAsync()).ValueOrThrow());
        Assert.False((await admin.EnsureDatabaseAsync()).ValueOrThrow());   // already there

        var name = Db.Provider.Name == "sqlite"
            ? Path.Combine(Path.GetTempPath(), $"cl-data-adm-{Guid.NewGuid():N}.db")
            : "cl_data_adm_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            Assert.False((await admin.DatabaseExistsAsync(name)).ValueOrThrow());
            (await admin.CreateDatabaseAsync(name)).ThrowIfFailure();
            Assert.True((await admin.DatabaseExistsAsync(name)).ValueOrThrow());
            (await admin.CreateDatabaseAsync(name)).ThrowIfFailure();   // no-op
        }
        finally
        {
            (await admin.DropDatabaseAsync(name)).ThrowIfFailure();
        }
        Assert.False((await admin.DatabaseExistsAsync(name)).ValueOrThrow());
    }

    [SkippableFact]
    public async Task Tables_can_be_checked_truncated_and_dropped()
    {
        RequireProvider();
        var admin = Db.Admin;
        (await admin.DropTableAsync<AdminItem>()).ThrowIfFailure();
        Assert.False((await admin.TableExistsAsync<AdminItem>()).ValueOrThrow());
        (await Db.Schema.SyncAsync<AdminItem>(new CL.Data.Schema.SchemaSyncOptions { Force = true })).ValueOrThrow();
        Assert.True((await admin.TableExistsAsync<AdminItem>()).ValueOrThrow());
        Assert.Contains("adm_items", (await admin.ListTablesAsync()).ValueOrThrow());

        var repo = Db.Repository<AdminItem>();
        (await repo.InsertManyAsync([new AdminItem { Name = "a" }, new AdminItem { Name = "b" }])).ValueOrThrow();
        (await admin.TruncateAsync<AdminItem>()).ThrowIfFailure();
        Assert.Equal(0L, (await Db.Query<AdminItem>().CountAsync()).ValueOrThrow());
        var next = (await repo.InsertAsync(new AdminItem { Name = "c" })).ValueOrThrow();
        if (Db.Provider.Name != "sqlite") Assert.Equal(1L, next.Id);   // identity restarts (SQLite keeps AUTOINCREMENT counters)

        (await admin.DropTableAsync<AdminItem>()).ThrowIfFailure();
        Assert.False((await admin.TableExistsAsync<AdminItem>()).ValueOrThrow());
    }

    [SkippableFact]
    public async Task Retention_purges_expired_rows_in_batches()
    {
        RequireProvider();
        (await Db.Admin.DropTableAsync<RetentionLog>()).ThrowIfFailure();
        (await Db.Schema.SyncAsync<RetentionLog>(new CL.Data.Schema.SchemaSyncOptions { Force = true })).ValueOrThrow();
        var now = DateTimeOffset.UtcNow;
        var rows = Enumerable.Range(0, 5).Select(i => new RetentionLog { At = now.AddDays(-10 - i), Message = $"old{i}" })
            .Concat([new RetentionLog { At = now.AddDays(-1), Message = "new1" }, new RetentionLog { At = now, Message = "new2" }])
            .ToList();
        (await Db.Repository<RetentionLog>().InsertManyAsync(rows)).ValueOrThrow();

        List<DataEvent> events = [];
        await using var db = Fixture.CreateDatabase(new DatabaseOptions { Events = events.Add });
        Assert.Equal(5L, (await db.Retention.PurgeAsync<RetentionLog>()).ValueOrThrow());
        Assert.Equal(["new1", "new2"], (await db.Query<RetentionLog>().OrderBy(r => r.Message).ToListAsync()).ValueOrThrow().Select(r => r.Message));
        Assert.Equal(5L, Assert.Single(events.OfType<RetentionPurgedEvent>()).Rows);
        Assert.Equal(0L, (await db.Retention.PurgeAsync<RetentionLog>()).ValueOrThrow());
    }

    [SkippableFact]
    public async Task Retention_from_a_tenant_view_purges_only_that_tenant()
    {
        RequireProvider();
        (await Db.Admin.DropTableAsync<TenantRetentionLog>()).ThrowIfFailure();
        (await Db.Schema.SyncAsync<TenantRetentionLog>(new CL.Data.Schema.SchemaSyncOptions { Force = true })).ValueOrThrow();
        var old = DateTimeOffset.UtcNow.AddDays(-30);
        foreach (var tenant in new[] { 1, 2 })
            (await Db.ForTenant(tenant).Repository<TenantRetentionLog>().InsertManyAsync([new TenantRetentionLog { At = old }, new TenantRetentionLog { At = old }])).ValueOrThrow();

        Assert.Equal(2L, (await Db.ForTenant(1).Retention.PurgeAsync<TenantRetentionLog>()).ValueOrThrow());
        Assert.Equal(2, (await Db.ForTenant(2).Query<TenantRetentionLog>().ToListAsync()).ValueOrThrow().Count);   // tenant 2 untouched

        Assert.Equal(2L, (await Db.Retention.PurgeAsync<TenantRetentionLog>()).ValueOrThrow());                   // the system-wide job
        Assert.Empty((await Db.ForTenant(2).Query<TenantRetentionLog>().ToListAsync()).ValueOrThrow());
    }
}
