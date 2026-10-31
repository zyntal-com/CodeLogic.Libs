using CL.Data.Configuration;
using CL.Data.PostgreSql;
using CL.Data.Providers;
using Data.Conformance;

namespace Data.PostgreSql.Tests;

[Collection("postgresql-replicas")]
public sealed class PostgreSqlLiveReplicaTests : LiveReplicaSuite
{
    protected override DataProvider Provider => PostgreSqlDataProvider.Instance;

    protected override DataConnectionConfig Primary() => new()
    {
        Provider = "postgresql", Host = "127.0.0.1", Port = 15533, Database = "cl_data", Username = "postgres", Password = "", SslMode = DataSslMode.Disable,
    };

    protected override (string Host, int Port) Replica => ("127.0.0.1", 15534);
    protected override int DeadPort => 15535;
    protected override string IsReplicaSql => "SELECT CASE WHEN pg_is_in_recovery() THEN 1 ELSE 0 END";
    protected override string PauseSql => "SELECT pg_wal_replay_pause()";
    protected override string ResumeSql => "SELECT pg_wal_replay_resume()";
}
