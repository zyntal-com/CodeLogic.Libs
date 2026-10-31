# Replicas & resilience

Every command passes through one pipeline:

```
interceptors → your middleware → routing (primary or replica) → resilience (retries, circuit breaker) → driver
```

Routing is added only when a connection has replicas. Telemetry wraps the whole pipeline when a listener is attached.

## Read replicas

```json
{
  "provider": "postgresql", "host": "pg-primary", "database": "app", "username": "app", "password": "secret",
  "replicas": [
    { "name": "r1", "host": "pg-replica-1", "weight": 3 },
    { "name": "r2", "host": "pg-replica-2" }
  ],
  "readPreference": "PreferReplica",
  "stickyAfterWriteMs": 2000,
  "maxReplicaLagMs": 5000,
  "replicaProbeIntervalSeconds": 10
}
```

A replica takes the primary's credentials and port unless it sets its own. On SQL Server, replicas are reached with
`ApplicationIntent=ReadOnly`, typically through an availability group listener.

**Routing rules:**
- **Writes, transactions and locking reads** always run on the primary.
- **Reads follow a preference:** `Primary`, `PreferReplica` or `ReplicaOnly`. Set it per connection (`readPreference`), per view (`db.ReadFrom(ReadPreference.Primary)`) or per query (`.ReadFrom(...)`).
- **Reads-your-writes:** for `stickyAfterWriteMs` after a write through a database, its reads stay on the primary.
- **Choosing a replica:** a replica is picked at random by weight, among those whose circuit isn't open and whose last probed lag is within `maxReplicaLagMs`.
- **No usable replica:** `PreferReplica` reads from the primary, and `ReplicaOnly` fails with `data.no_healthy_node`.
- **Replica down mid-read:** a `PreferReplica` read whose replica turns out to be down (unreachable, or circuit open) runs again on the primary.

```csharp
var fresh = await db.Query<Order>().ReadFrom(ReadPreference.Primary).Where(o => o.Id == id).FirstOrDefaultAsync();
var reporting = db.ReadFrom(ReadPreference.ReplicaOnly);        // a cheap view sharing the pools
var stats = await reporting.Query<Order>().GroupBy(o => o.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync();
```

## Lag probes

Each server provider measures lag on its replicas, and `DataLibrary` runs the probes every `replicaProbeIntervalSeconds`.
Without the runtime, call `db.StartReplicaProbes(scheduler)`, or call `db.ProbeReplicasAsync()` yourself.

**How lag is reported:**
- `DataNode.LagMs` is 0 when the replica is caught up, the lag otherwise, and `ReplicaProbe.Broken` when replication is stopped (always skipped).
- It's `null` when unknown: not a replica, or a missing probe privilege. An unknown lag counts as usable.
- A probe that can't reach its replica counts as a failure for the replica's circuit breaker. A successful probe closes the circuit again, so a recovered replica returns to rotation.

## Primary failover

Failover of the primary is left to the drivers, configured through CL.Data:

| Provider | Setting | Driver feature |
|---|---|---|
| MySQL | `failoverHosts: ["db1b", ...]` | MySqlConnector failover load balancing |
| PostgreSQL | `failoverHosts: ["pg2", ...]` | Npgsql multi-host, `TargetSessionAttributes=primary` |
| SQL Server | `sqlServer.multiSubnetFailover`, `sqlServer.failoverPartner` | availability group listeners, database mirroring |

## Retries

`retryCount` (default 3) and `retryBaseDelayMs` (default 50) control retries with exponential backoff and jitter.

**What is retried:**
- **Reads** retry every transient failure.
- **Writes** retry only when they certainly didn't take effect: the connection couldn't be opened, or the server rolled the statement back (deadlock, lock timeout).
- **Never retried:** a transient failure after a write was sent, and a failed commit, because the outcome is unknown.
- **User transactions:** nothing inside them is retried, since the transaction can't be used after a failure. Wrap the block in `db.InTransactionAsync(..., maxAttempts: 3)` to retry it as a whole on deadlocks.

After the last attempt you get the original error (`data.deadlock`, `data.unavailable`, ...), not a wrapper.

## Circuit breakers

Each node has its own breaker (`DataNode.Breaker`), configured by `circuitBreakerThreshold` (default 5) and `circuitBreakSeconds` (default 30).

**How it behaves:**
- **What counts:** only node failures, meaning the node is unreachable or the connection was lost. Deadlocks, slow queries and constraint errors never open a circuit.
- **While open:** commands to that node fail fast with `data.circuit_open`, and replicas are skipped.
- **Recovery:** after the break, one trial call (or a probe) closes the circuit again when it succeeds.
- **Events:** every transition publishes `CircuitStateChangedEvent`, and openings increment `cl.data.circuit.opened`.

## Health

```csharp
foreach (var node in await db.CheckHealthAsync())
    Console.WriteLine($"{node.Node}: reachable={node.Reachable} circuit={node.Circuit} lag={node.LagMs} ({node.Latency.TotalMilliseconds:F1} ms)");
```

**Library health:**
- `DataLibrary.HealthCheckAsync` is **Unhealthy** when a primary can't be reached.
- It's **Degraded** when a replica is down or lagging, or a connection failed to start.
- It publishes `NodeHealthChangedEvent` whenever a node's state changes.

## Testing against real replicas

`tests/replicas/docker-compose.yml` starts a MySQL 8.4 GTID pair and a PostgreSQL 18 streaming pair. The live suite runs with `CL_DATA_REPLICAS=1`.
