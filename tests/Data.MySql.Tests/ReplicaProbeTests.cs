using System.Data;
using CL.Data.MySql;
using CL.Data.Routing;

namespace Data.MySql.Tests;

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
    public async Task Caught_up() => Assert.Equal(0L, await Lag(new MySqlReplicaProbe(), [("Seconds_Behind_Source", 0L)]));

    [Fact]
    public async Task Behind() => Assert.Equal(7000L, await Lag(new MySqlReplicaProbe(), [("Seconds_Behind_Source", 7L)]));

    [Fact]
    public async Task Stopped() => Assert.Equal(ReplicaProbe.Broken, await Lag(new MySqlReplicaProbe(), [("Seconds_Behind_Source", DBNull.Value)]));
}
