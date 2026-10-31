using System.Data;
using CL.Data.Mapping;

namespace Data.Tests.Mapping;

public sealed class ValueReaderTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Decimal_materialization_preserves_source_round_trip_precision(bool singlePrecision)
    {
        using var table = new DataTable();
        table.Columns.Add("Amount", singlePrecision ? typeof(float) : typeof(double));
        table.Rows.Add(singlePrecision ? (object)0.1f : 0.1d);
        using var reader = table.CreateDataReader();
        Assert.True(reader.Read());
        Assert.Equal(0.1m, ValueReader<decimal>.Read(reader, 0));
        Assert.Equal(0.1m, ValueReader<decimal?>.Read(reader, 0));
    }
}
