# Telemetry

CL.Data publishes OpenTelemetry spans and metrics, follows the database semantic conventions, and adds diagnostics
events: slow queries with their plan, N+1 patterns, per-command events, circuit and health changes. With no listener
attached it costs nothing: no span is created and nothing extra is allocated.

## OpenTelemetry

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource("CL.Data"))
    .WithMetrics(m => m.AddMeter("CL.Data"));
```

**Spans:** one client span per logical command, with retries included. It's named `{operation} {table}`, for example `select products`.

| Tag | Value |
|---|---|
| `db.system.name` | `mysql`, `postgresql`, `microsoft.sql_server`, `sqlite` |
| `db.namespace` | connection name |
| `db.operation.name` / `db.collection.name` | `select`, `insert`, `update`, `delete`, `upsert`, `raw` / main table |
| `db.query.text` | the parameterized SQL (placeholders only, never values) |
| `server.address`, `server.port`, `cl.data.node` | the node that served the command (primary or replica name) |
| `db.response.returned_rows` | rows read or affected |
| `cl.data.attempts` | attempts, when the command was retried |
| `error.type` + status `Error` | the `data.*` error code on failure |

**Context:** a span becomes `Activity.Current` for the driver, so driver spans nest under it. It never leaks back into the calling code.

**Metrics:**

| Instrument | Unit | Notes |
|---|---|---|
| `db.client.operation.duration` | s | histogram; tags `db.system.name`, `db.namespace`, `db.operation.name`, `db.collection.name`, `error.type` |
| `cl.data.rows` | {row} | rows read or affected |
| `cl.data.retries` | {retry} | tagged with the error that caused the retry |
| `cl.data.cache.hits` / `cl.data.cache.misses` | | result cache |
| `cl.data.circuit.opened` | {event} | per node |
| `cl.data.replica.lag` | ms | observable gauge per replica |

Connection-pool metrics come from the drivers' own meters: MySqlConnector, Npgsql and SqlClient each publish them.

## Diagnostics

| Setting | Event | What happens |
|---|---|---|
| `slowQueryThresholdMs` (default 1000) | `SlowQueryEvent` | Commands slower than the threshold are reported with their SQL and duration. |
| `captureExplainOnSlowQuery` | `SlowQueryEvent.Plan` | For slow reads outside transactions, the plan is captured on a separate connection in the background, without running the statement. The caller never waits for it. |
| `nPlusOneThreshold` | `NPlusOneDetectedEvent` | The same parameterized query running this many times within a second is reported once per window. |
| `publishQueryEvents` | `QueryExecutedEvent` | Every command is reported, with operation, table, duration, rows and success. This is frequent, so it's off by default. |
| `data.cache.publishEvents` | `CacheHitEvent` / `CacheMissEvent` | Every cached read is reported. |

**Plan formats:**
- MySQL: `EXPLAIN FORMAT=TREE`.
- PostgreSQL: `EXPLAIN`.
- SQLite: `EXPLAIN QUERY PLAN`.
- SQL Server: `SET SHOWPLAN_TEXT`, with the parameters declared as variables of the same types.

**Granularity:** slow-query and N+1 checks run per attempt, so a retried command can report each slow attempt.

## Events

**Without the runtime:** events go to `DatabaseOptions.Events`.

**With `DataLibrary`:** they're published on the CodeLogic event bus under their concrete types. The important ones are also logged through the localized `DataStrings` messages.

```csharp
events.Subscribe<SlowQueryEvent>(e => logger.Warning($"{e.ConnectionId}: {e.Duration.TotalMilliseconds} ms\n{e.Sql}\n{e.Plan}"));
events.Subscribe<SchemaDriftDetectedEvent>(e => alert($"{e.Table}: {string.Join(", ", e.PendingChanges)}"));
```

| Event | When |
|---|---|
| `QueryExecutedEvent`, `SlowQueryEvent`, `NPlusOneDetectedEvent` | diagnostics (above) |
| `CacheHitEvent`, `CacheMissEvent` | cached reads, when enabled |
| `SchemaSyncedEvent`, `SchemaDriftDetectedEvent` | table sync |
| `MigrationAppliedEvent`, `SeedAppliedEvent` | migrations (also rollbacks) and seeds |
| `RetentionPurgedEvent` | expired rows deleted |
| `BulkCopyCompletedEvent` | a bulk copy finished |
| `CircuitStateChangedEvent`, `NodeHealthChangedEvent` | a node's circuit or health changed |
| `CacheInvalidatedEvent` | cross-node invalidations on the `Events` bus |

## Interceptors and middleware

To observe or change commands yourself, use an `IDataInterceptor` or an `ICommandMiddleware` in `DatabaseOptions`:
- **Interceptors** run first and see the SQL, operation, table, node, rows and elapsed time.
- **Middleware** can change the SQL, pick the node, or return a result itself.

```csharp
sealed class TagQueries : IDataInterceptor
{
    public ValueTask<Error?> OnExecutingAsync(CommandContext context)
    {
        context.Spec = context.Spec with { Sql = "/* app=orders */ " + context.Spec.Sql };
        return ValueTask.FromResult<Error?>(null);
    }
}
```
