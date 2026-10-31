using System.Data;
using CL.Data.SqlServer;
using CL.Data.Routing;

namespace Data.SqlServer.Tests;

/// <summary>Replica lag parsing against canned server output.</summary>
public sealed class ReplicaProbeTests
{
    private static async Task<long?> Lag(ReplicaProbe probe, (string Name, object Value)[] columns)
    {
        var table = new DataTable();
        foreach (var (name, value) in columns) table.Columns.Add(name, value is DBNull ? typeof(object) : value.GetType());
        table.Rows.Add(columns.Select(c => c.Value).ToArray());
        using var reader = table.CreateDataReader();
        Assert.True(await reader.ReadAsync());
        return await probe.ReadLagMsAsync(reader, CancellationToken.None);
    }

    [Fact]
    public async Task Caught_up() => Assert.Equal(0L, await Lag(new SqlServerReplicaProbe(), [("secondary_lag_seconds", 0L), ("synchronization_state_desc", "SYNCHRONIZED")]));

    [Fact]
    public async Task Behind() => Assert.Equal(3000L, await Lag(new SqlServerReplicaProbe(), [("secondary_lag_seconds", 3L), ("synchronization_state_desc", "SYNCHRONIZING")]));

    [Fact]
    public async Task Stopped() => Assert.Equal(ReplicaProbe.Broken, await Lag(new SqlServerReplicaProbe(), [("secondary_lag_seconds", DBNull.Value), ("synchronization_state_desc", "NOT SYNCHRONIZING")]));
}
