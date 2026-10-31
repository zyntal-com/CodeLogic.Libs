using System.Data;
using CL.Data;
using CL.Data.Mapping;

namespace Data.Tests.Mapping;

[Table(Name = "users")]
[CompositeIndex("ix_users_tenant_email", nameof(TenantId), nameof(Email), Unique = true)]
[MySqlTable(Engine = "InnoDB")]
public class TestUser
{
    [Column(Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Size = 320)] public string Email { get; set; } = "";
    public string? DisplayName { get; set; }
    [TenantKey] public int TenantId { get; set; }
    [ConcurrencyToken] public int Version { get; set; }
    [CreatedAt] public DateTimeOffset CreatedAt { get; set; }
    [UpdatedAt] public DateTimeOffset? UpdatedAt { get; set; }
    [SoftDelete] public DateTimeOffset? DeletedAt { get; set; }
    [Index(Name = "ix_users_score", Order = 1, Direction = SortOrder.Descending)] public decimal Score { get; set; }
    [ForeignKey<TestGroup>(nameof(TestGroup.Id), OnDelete = ForeignKeyAction.SetNull)] public int? GroupId { get; set; }
    [Column(Name = "role_code")] [MySqlColumn(Charset = "ascii")] public TestRole Role { get; set; }
    [Ignore] public string Computed => Email.ToUpperInvariant();
    public Guid ExternalId { get; set; }
}

public enum TestRole { User = 1, Admin = 2 }

[Table]
public class TestGroup
{
    [Column(Primary = true)] public int Id { get; set; }
}

[Table]
public class TestMembership
{
    [Column(Primary = true, KeyOrder = 2)] public long GroupId { get; set; }
    [Column(Primary = true, KeyOrder = 1)] public long UserId { get; set; }
    [RetainDays(30)] public DateTime JoinedAt { get; set; }
}

[DataRow]
public sealed record TestRow(long Id, string Name, decimal? Amount);

public sealed class MappingTests
{
    private static EntityMap MapOf<T>() => EntityAccessorRegistry.Get<T>().ValueOrThrow().Map;

    [Theory]
    [InlineData("UserId", "user_id")]
    [InlineData("Id", "id")]
    [InlineData("HTTPServerURL", "http_server_url")]
    [InlineData("Address2Line", "address2_line")]
    [InlineData("already_snake", "already_snake")]
    [InlineData("IPAddress", "ip_address")]
    public void Snake_case(string input, string expected) => Assert.Equal(expected, NameConverter.ToSnakeCase(input));

    [Theory]
    [InlineData("CreatedAt", "createdAt")]
    [InlineData("ID", "id")]
    [InlineData("IDValue", "idValue")]
    [InlineData("name", "name")]
    public void Camel_case(string input, string expected) => Assert.Equal(expected, NameConverter.ToCamelCase(input));

    [Fact]
    public void Reflection_map_reads_columns_roles_and_nullability()
    {
        var map = MapOf<TestUser>();
        Assert.True(map.IsTable);
        Assert.Equal("users", map.ExplicitTableName);
        Assert.DoesNotContain(map.Columns, c => c.PropertyName == nameof(TestUser.Computed));

        Assert.Equal([nameof(TestUser.Id)], map.Key.Columns.Select(c => c.PropertyName));
        Assert.Same(map.Key.Columns[0], map.Identity);
        Assert.Equal(nameof(TestUser.TenantId), map.TenantKey!.PropertyName);
        Assert.Equal(nameof(TestUser.Version), map.ConcurrencyToken!.PropertyName);
        Assert.Equal(nameof(TestUser.CreatedAt), map.CreatedAt!.PropertyName);
        Assert.Equal(nameof(TestUser.UpdatedAt), map.UpdatedAt!.PropertyName);
        Assert.Equal(nameof(TestUser.DeletedAt), map.SoftDelete!.PropertyName);

        Assert.False(map.Property(nameof(TestUser.Email)).IsNullable);          // string (NRT not-null)
        Assert.True(map.Property(nameof(TestUser.DisplayName)).IsNullable);     // string?
        Assert.True(map.Property(nameof(TestUser.GroupId)).IsNullable);         // int?
        Assert.Equal(DataType.String, map.Property(nameof(TestUser.Email)).DataType);
        Assert.Equal(320, map.Property(nameof(TestUser.Email)).Size);
        Assert.Equal(DataType.Int, map.Property(nameof(TestUser.Role)).DataType);   // enum → underlying int
        Assert.Equal(DataType.Guid, map.Property(nameof(TestUser.ExternalId)).DataType);
        Assert.Equal("ascii", map.Property(nameof(TestUser.Role)).Hint<MySqlColumnAttribute>()!.Charset);
        Assert.Equal("InnoDB", map.Hint<MySqlTableAttribute>()!.Engine);
    }

    [Fact]
    public void Indexes_and_foreign_keys()
    {
        var map = MapOf<TestUser>();
        var composite = Assert.Single(map.Indexes, i => i.Name == "ix_users_tenant_email");
        Assert.True(composite.Unique);
        Assert.Equal([nameof(TestUser.TenantId), nameof(TestUser.Email)], composite.Columns.Select(c => c.Column.PropertyName));

        var score = Assert.Single(map.Indexes, i => i.Name == "ix_users_score");
        Assert.Equal(SortOrder.Descending, score.Columns[0].Direction);

        var fk = Assert.Single(map.ForeignKeys);
        Assert.Equal(typeof(TestGroup), fk.ReferencedEntity);
        Assert.Equal(nameof(TestGroup.Id), fk.ReferencedColumn);
        Assert.Equal(ForeignKeyAction.SetNull, fk.OnDelete);
    }

    [Fact]
    public void Composite_key_follows_key_order_and_retention_is_read()
    {
        var map = MapOf<TestMembership>();
        Assert.True(map.Key.IsComposite);
        Assert.Equal([nameof(TestMembership.UserId), nameof(TestMembership.GroupId)], map.Key.Columns.Select(c => c.PropertyName));
        Assert.Equal(30, map.Retention!.RetainDays);
    }

    [Fact]
    public void Names_follow_the_naming_policy_but_explicit_names_win()
    {
        var map = MapOf<TestUser>();
        var snake = map.GetNames(NamingPolicy.SnakeCase);
        Assert.Equal("users", snake.Table);                                    // explicit
        Assert.Equal("display_name", snake.Of(map.Property(nameof(TestUser.DisplayName))));
        Assert.Equal("role_code", snake.Of(map.Property(nameof(TestUser.Role))));   // explicit
        Assert.Same(map.Property(nameof(TestUser.DisplayName)), snake.ByColumn["DISPLAY_NAME"]);
        Assert.Same(snake, map.GetNames(NamingPolicy.SnakeCase));              // cached

        var group = MapOf<TestGroup>();
        Assert.Equal("TestGroup", group.GetNames(NamingPolicy.AsIs).Table);
        Assert.Equal("test_group", group.GetNames(NamingPolicy.SnakeCase).Table);
    }

    [Fact]
    public void Invalid_models_list_every_problem()
    {
        ColumnMap Col(string name, Type type, ColumnRoles roles = ColumnRoles.None, int keyOrder = 0, bool nullable = false, string? explicitName = null) =>
            new() { PropertyName = name, ClrType = type, Roles = roles, KeyOrder = keyOrder, IsNullable = nullable, ExplicitName = explicitName };

        var ex = Assert.Throws<InvalidOperationException>(() => EntityMap.Create(typeof(object), new TableAttribute(),
        [
            Col("A", typeof(long), ColumnRoles.Key),
            Col("B", typeof(long), ColumnRoles.Key),                    // composite without KeyOrder
            Col("C", typeof(string), ColumnRoles.Identity),             // identity not integer
            Col("D", typeof(DateTimeOffset), ColumnRoles.SoftDelete),   // soft delete not nullable
            Col("E", typeof(string), explicitName: "dup"),
            Col("F", typeof(string), explicitName: "DUP"),              // duplicate column name
            Col("G", typeof(Guid), ColumnRoles.ConcurrencyToken),       // wrong token type
            Col("H", typeof(string), explicitName: "bad`name"),         // unsafe identifier
        ]));

        Assert.Contains("KeyOrder", ex.Message);
        Assert.Contains("AutoIncrement column 'C'", ex.Message);
        Assert.Contains("[SoftDelete] column 'D'", ex.Message);
        Assert.Contains("'dup'", ex.Message);
        Assert.Contains("[ConcurrencyToken] column 'G'", ex.Message);
        Assert.Contains("bad`name", ex.Message);
    }

    [Fact]
    public void Entity_key_normalizes_integers_and_compares_composites()
    {
        Assert.Equal(EntityKey.Of(5), EntityKey.Of(5L));
        Assert.Equal(EntityKey.Of(5).GetHashCode(), EntityKey.Of(5L).GetHashCode());
        Assert.Equal(EntityKey.Of(1, "a"), EntityKey.Of(1L, "a"));
        Assert.NotEqual(EntityKey.Of(1, "a"), EntityKey.Of(1, "b"));
        Assert.Equal(EntityKey.Of(new byte[] { 1, 2 }), EntityKey.Of(new byte[] { 1, 2 }));
        Assert.Equal("42", EntityKey.Of(42).ToString());
        Assert.Equal("(7|abc)", EntityKey.Of(7, "abc").ToString());
        Assert.Equal(0, default(EntityKey).Count);
    }

    [Fact]
    public void Reflection_accessor_materializes_converts_and_writes_back()
    {
        var accessor = EntityAccessorRegistry.Get<TestUser>().ValueOrThrow();
        var map = accessor.Map;
        var names = map.GetNames(NamingPolicy.AsIs);

        var table = new DataTable();
        table.Columns.Add("Id", typeof(long));
        table.Columns.Add("Email", typeof(string));
        table.Columns.Add("role_code", typeof(long));          // SQLite-style integer for an enum
        table.Columns.Add("CreatedAt", typeof(DateTime));      // DateTime for a DateTimeOffset
        table.Columns.Add("ExternalId", typeof(byte[]));       // 16 bytes for a Guid
        table.Columns.Add("Score", typeof(double));            // double for a decimal
        table.Columns.Add("GroupId", typeof(int));
        var guid = Guid.NewGuid();
        table.Rows.Add(7L, "a@b.c", 2L, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified), guid.ToByteArray(), 0.1d, DBNull.Value);

        using var reader = table.CreateDataReader();
        Assert.True(reader.Read());
        var ordinals = map.Columns.Select(c => table.Columns.IndexOf(names.Of(c))).ToArray();
        var user = accessor.Materialize(reader, ordinals);

        Assert.Equal(7, user.Id);
        Assert.Equal("a@b.c", user.Email);
        Assert.Equal(TestRole.Admin, user.Role);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), user.CreatedAt);
        Assert.Equal(guid, user.ExternalId);
        Assert.Equal(0.1m, user.Score);                        // round-trip text, not the exact binary value
        Assert.Null(user.GroupId);
        Assert.Null(user.DisplayName);                         // absent column keeps its default

        accessor.SetValue(user, map.Identity!.Ordinal, 99L);
        Assert.Equal(99, user.Id);
        accessor.SetValue(user, map.Property(nameof(TestUser.Version)).Ordinal, 3L);   // converted to int
        Assert.Equal(3, user.Version);
        Assert.Equal(EntityKey.Of(99), accessor.GetKey(user));
        Assert.Equal("a@b.c", accessor.GetValue(user, map.Property(nameof(TestUser.Email)).Ordinal));
    }

    [Fact]
    public void Records_are_materialized_through_their_constructor()
    {
        var accessor = EntityAccessorRegistry.Get<TestRow>().ValueOrThrow();
        Assert.False(accessor.Map.IsTable);

        var table = new DataTable();
        table.Columns.Add("Id", typeof(int));
        table.Columns.Add("Name", typeof(string));
        table.Columns.Add("Amount", typeof(decimal));
        table.Rows.Add(3, "x", DBNull.Value);
        using var reader = table.CreateDataReader();
        reader.Read();

        var row = accessor.Materialize(reader, [0, 1, 2]);
        Assert.Equal(new TestRow(3, "x", null), row);
    }

    [Fact]
    public void Column_writer_receives_typed_values()
    {
        var accessor = EntityAccessorRegistry.Get<TestGroup>().ValueOrThrow();
        var writer = new RecordingWriter();
        accessor.WriteColumn(new TestGroup { Id = 12 }, 0, writer);
        Assert.Equal(12, writer.Value);
    }

    [Fact]
    public void Reflection_can_be_switched_off()
    {
        EntityAccessorRegistry.ResetReflectionCache<TestGroup>();
        EntityAccessorRegistry.AllowReflection = false;
        try
        {
            var result = EntityAccessorRegistry.Get<TestGroup>();
            Assert.True(result.IsFailure);
            Assert.Equal(CL.Data.Errors.DataErrors.EntityNotMappedCode, result.Error!.Code);
        }
        finally
        {
            EntityAccessorRegistry.AllowReflection = true;
        }
    }

    private sealed class RecordingWriter : IColumnValueWriter
    {
        public object? Value;
        public void Write<TValue>(ColumnMap column, TValue value) => Value = value;
    }
}
