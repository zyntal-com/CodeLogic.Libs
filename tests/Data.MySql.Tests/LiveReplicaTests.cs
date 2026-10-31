using CL.Data.Configuration;
using CL.Data.MySql;
using CL.Data.Providers;
using Data.Conformance;

namespace Data.MySql.Tests;

[Collection("mysql-replicas")]
public sealed class MySqlLiveReplicaTests : LiveReplicaSuite
{
    protected override DataProvider Provider => MySqlDataProvider.Instance;

    protected override DataConnectionConfig Primary() => new()
    {
        Provider = "mysql", Host = "127.0.0.1", Port = 33407, Database = "cl_data", Username = "root", Password = "", SslMode = DataSslMode.Disable,
    };

    protected override (string Host, int Port) Replica => ("127.0.0.1", 33408);
    protected override int DeadPort => 33409;
    protected override string IsReplicaSql => "SELECT CASE WHEN @@server_id = 2 THEN 1 ELSE 0 END";
    protected override string PauseSql => "STOP REPLICA SQL_THREAD";
    protected override string ResumeSql => "START REPLICA SQL_THREAD";
}
