using System.Text.Json.Serialization;
using CL.Data;
using CL.Data.Configuration;
using CL.Data.Execution;
using CL.Data.Mapping;
using CL.Data.Mapping.Conversion;
using CL.Data.Providers;
using CL.Data.Sqlite;
using CodeLogic.Core.Results;

namespace Data.Sqlite.Tests;

public readonly record struct OrderId(long Value) : IDataValue<OrderId, long>
{
    public static OrderId FromStore(long value) => new(value);
    public long ToStore() => Value;
}

public enum OrderStatus { Pending, Shipped, Cancelled }

public sealed record OrderSettings(bool Gift, string[] Tags);

[JsonSerializable(typeof(OrderSettings))]
internal sealed partial class TestJsonContext : JsonSerializerContext;

[Table(Name = "orders")]
public sealed class Order
{
    [Column(Primary = true)] public OrderId Id { get; set; }
    [Column(Converter = typeof(EnumToStringConverter<OrderStatus>))] public OrderStatus Status { get; set; }
    public OrderSettings? Settings { get; set; }
}

public sealed class ConverterTests : IAsyncLifetime
{
    private Database _db = null!;

    public async Task InitializeAsync()
    {
        DataProviderRegistry.Register(SqliteDataProvider.Instance);
        ValueConverters.RegisterDataValue<OrderId, long>();
        ValueConverters.RegisterJson(TestJsonContext.Default.OrderSettings);
        _db = Database.Create("conv", new DataConnectionConfig { Provider = "sqlite", Database = ":memory:" }).ValueOrThrow();
        (await _db.ExecuteSqlAsync($"CREATE TABLE orders (Id INTEGER PRIMARY KEY, Status TEXT, Settings TEXT)")).ValueOrThrow();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public void Map_uses_the_store_types_of_converters()
    {
        var map = EntityAccessorRegistry.Get<Order>().ValueOrThrow().Map;
        Assert.Equal(DataType.BigInt, map.Property(nameof(Order.Id)).DataType);
        Assert.Equal(DataType.String, map.Property(nameof(Order.Status)).DataType);
        Assert.Equal(DataType.Json, map.Property(nameof(Order.Settings)).DataType);
        Assert.IsType<EnumToStringConverter<OrderStatus>>(map.Property(nameof(Order.Status)).ExplicitConverter);
    }

    [Fact]
    public async Task Typed_ids_and_json_round_trip_through_raw_sql()
    {
        var id = new OrderId(42);
        var settings = new OrderSettings(true, ["fragile"]);
        (await _db.ExecuteSqlAsync($"INSERT INTO orders VALUES ({id}, {"Shipped"}, {settings})")).ValueOrThrow();

        // Stored forms: the long, the enum name, the JSON text.
        Assert.Equal(42L, (await _db.SqlScalarAsync<long>($"SELECT Id FROM orders")).ValueOrThrow());
        Assert.Contains("\"fragile\"", (await _db.SqlScalarAsync<string>($"SELECT Settings FROM orders")).ValueOrThrow());

        var order = Assert.Single((await _db.SqlQueryAsync<Order>($"SELECT * FROM orders WHERE Id = {id}")).ValueOrThrow());
        Assert.Equal(id, order.Id);
        Assert.Equal(OrderStatus.Shipped, order.Status);
        Assert.Equal(settings.Tags, order.Settings!.Tags);
        Assert.True(order.Settings.Gift);

        Assert.Equal(id, (await _db.SqlScalarAsync<OrderId>($"SELECT Id FROM orders")).ValueOrThrow());
        Assert.Equal([id], (await _db.SqlQueryAsync<OrderId>($"SELECT Id FROM orders")).ValueOrThrow());
    }
}
