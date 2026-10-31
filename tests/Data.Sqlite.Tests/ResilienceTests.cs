using System.Data.Common;
using CL.Data.Configuration;
using CL.Data.Errors;
using CL.Data.Execution;
using CL.Data.Providers;
using CL.Data.Sql;
using CL.Data.Sqlite;
using CodeLogic.Core.Results;

namespace Data.Sqlite.Tests;

/// <summary>Retries and the circuit breaker, driven by a SQLite provider with injected faults.</summary>
public sealed class ResilienceTests : IAsyncLifetime
{
    /// <summary>A failure the faulty classifier reports as transient.</summary>
    private sealed class FaultException(string message) : DbException(message);

    /// <summary>The real SQLite data source, failing the next <see cref="OpenFaults"/> connection opens.</summary>
    private sealed class FaultySource(DbDataSource inner) : DbDataSource
    {
        public int OpenFaults;
        public int Opens;

        public override string ConnectionString => inner.ConnectionString;
        protected override DbConnection CreateDbConnection() => inner.CreateConnection();

        protected override async ValueTask<DbConnection> OpenDbConnectionAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Opens);
            if (Interlocked.Decrement(ref OpenFaults) >= 0) throw new FaultException("cl_fault_open: server unreachable");
            return await inner.OpenConnectionAsync(cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>SQLite's classifier plus the injected faults: unreachable (open), deadlock and an unknown transient error.</summary>
    private sealed class FaultyClassifier : IErrorClassifier
    {
        public ErrorClassification Classify(Exception exception)
        {
            var message = exception.Message;
            if (message.Contains("cl_fault_open", StringComparison.Ordinal))
                return new(DataErrors.Unavailable("unreachable", message), true);
            if (message.Contains("cl_fault_deadlock", StringComparison.Ordinal))
                return new(DataErrors.Deadlock(message), true);
            if (message.Contains("cl_fault_transient", StringComparison.Ordinal))
                return new(DataErrors.ProviderError("connection reset", message), true);
            return SqliteDataProvider.Instance.Errors.Classify(exception);
        }
    }

    private sealed class FaultyProvider : DataProvider
    {
        private static readonly SqliteDataProvider Inner = SqliteDataProvider.Instance;
        public FaultySource? Source;

        public override string Name => "sqlite";
        public override ProviderCapabilities Capabilities => Inner.Capabilities;
        public override SqlDialect Dialect => Inner.Dialect;
        public override ITypeMapper Types => Inner.Types;
        public override IErrorClassifier Errors { get; } = new FaultyClassifier();
        public override CL.Data.Schema.DdlGenerator Ddl => Inner.Ddl;
        public override CL.Data.Schema.SchemaIntrospector? Introspector => Inner.Introspector;
        public override object? ReadLastInsertId(DbCommand command) => Inner.ReadLastInsertId(command);
        public override Result<string> BuildConnectionString(DataConnectionConfig config, NodeEndpoint node) => Inner.BuildConnectionString(config, node);

        public override DbDataSource CreateDataSource(DataConnectionConfig config, NodeEndpoint node, string connectionString) =>
            Source = new FaultySource(Inner.CreateDataSource(config, node, connectionString));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly string _file = Path.Combine(Path.GetTempPath(), $"cl-data-res-{Guid.NewGuid():N}.db");
    private readonly List<Database> _created = [];

    private (Database Db, FaultySource Source) Create(int retries = 2, int breakerThreshold = 0, Clock? clock = null)
    {
        var provider = new FaultyProvider();
        var db = Database.Create("res", provider, new DataConnectionConfig
        {
            Provider = "sqlite",
            Database = _file,
            RetryCount = retries,
            RetryBaseDelayMs = 0,
            CircuitBreakerThreshold = breakerThreshold,
            CircuitBreakSeconds = 30,
        }, new DatabaseOptions { Time = clock ?? (TimeProvider)TimeProvider.System }).ValueOrThrow();
        _created.Add(db);
        return (db, provider.Source!);
    }

    public async Task InitializeAsync()
    {
        var (db, _) = Create();
        (await db.ExecuteSqlAsync($"CREATE TABLE res_items (v TEXT NOT NULL)")).ValueOrThrow();
        (await db.ExecuteSqlAsync(SqlText.Raw(
            "CREATE TRIGGER res_fault BEFORE INSERT ON res_items BEGIN " +
            "SELECT RAISE(ABORT, 'cl_fault_deadlock') WHERE NEW.v = 'deadlock'; " +
            "SELECT RAISE(ABORT, 'cl_fault_transient') WHERE NEW.v = 'transient'; END"))).ValueOrThrow();
    }

    public async Task DisposeAsync()
    {
        foreach (var db in _created) await db.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_file); } catch (IOException) { }
    }

    private static Task<Result<int>> Insert(Database db, string value) =>
        db.ExecuteSqlAsync($"INSERT INTO res_items (v) VALUES ({value})").AsTask();

    private static async Task<long> Count(Database db) =>
        (await db.SqlScalarAsync<long>($"SELECT COUNT(*) FROM res_items")).ValueOrThrow();

    [Fact]
    public async Task Reads_and_writes_retry_when_the_connection_cannot_be_opened()
    {
        var (db, source) = Create(retries: 2);
        source.OpenFaults = 2;
        Assert.Equal(0L, await Count(db));
        Assert.Equal(3, source.Opens);

        source.OpenFaults = 2;
        (await Insert(db, "a")).ValueOrThrow();
        Assert.Equal(1L, await Count(db));                                   // written exactly once

        source.OpenFaults = 5;                                               // more faults than retries
        var failed = await Insert(db, "b");
        Assert.Equal(DataErrors.UnavailableCode, failed.Error!.Code);        // the CL.Data error, not a wrapper
    }

    [Fact]
    public async Task Writes_retry_only_when_the_statement_certainly_did_not_apply()
    {
        var (db, source) = Create(retries: 2);
        var before = source.Opens;
        var deadlock = await Insert(db, "deadlock");                        // rolled back by the server: safe to retry
        Assert.Equal(DataErrors.DeadlockCode, deadlock.Error!.Code);
        Assert.Equal(before + 3, source.Opens);

        before = source.Opens;
        var transient = await Insert(db, "transient");                      // outcome unknown: never retried
        Assert.Equal(DataErrors.ProviderErrorCode, transient.Error!.Code);
        Assert.Equal(before + 1, source.Opens);
    }

    [Fact]
    public async Task Transient_read_failures_retry_but_permanent_ones_do_not()
    {
        var (db, source) = Create(retries: 2);
        var before = source.Opens;
        var transient = await db.SqlScalarAsync<long>($"SELECT COUNT(*) FROM cl_fault_transient");
        Assert.True(transient.IsFailure);
        Assert.Equal(before + 3, source.Opens);

        before = source.Opens;
        Assert.True((await db.SqlScalarAsync<long>($"SELECT COUNT(*) FROM no_such_table")).IsFailure);
        Assert.Equal(before + 1, source.Opens);
    }

    [Fact]
    public async Task Nothing_retries_inside_a_transaction()
    {
        var (db, source) = Create(retries: 3);
        var tx = (await db.BeginTransactionAsync()).ValueOrThrow();
        await using (tx)
        {
            var opens = source.Opens;
            var result = await tx.Db.ExecuteSqlAsync($"INSERT INTO res_items (v) VALUES ({"deadlock"})");
            Assert.Equal(DataErrors.DeadlockCode, result.Error!.Code);
            Assert.Equal(opens, source.Opens);
        }
    }

    [Fact]
    public async Task The_breaker_opens_after_node_failures_and_recovers_after_the_break()
    {
        var clock = new Clock();
        var (db, source) = Create(retries: 0, breakerThreshold: 2, clock: clock);
        source.OpenFaults = int.MaxValue;
        Assert.Equal(DataErrors.UnavailableCode, (await Insert(db, "x")).Error!.Code);
        Assert.Equal(DataErrors.UnavailableCode, (await Insert(db, "x")).Error!.Code);
        Assert.False(db.Primary.IsHealthy);

        var opens = source.Opens;
        Assert.Equal(DataErrors.CircuitOpenCode, (await Insert(db, "x")).Error!.Code);
        Assert.Equal(opens, source.Opens);                                   // failed fast, nothing sent

        // Deadlocks and constraint errors are not node failures and never open the circuit.
        source.OpenFaults = 0;
        clock.Now = clock.Now.AddSeconds(31);
        (await Insert(db, "ok")).ValueOrThrow();                             // the trial call closes it again
        Assert.True(db.Primary.IsHealthy);
        for (var i = 0; i < 5; i++) Assert.Equal(DataErrors.DeadlockCode, (await Insert(db, "deadlock")).Error!.Code);
        Assert.True(db.Primary.IsHealthy);
    }
}
