using CL.Data;
using CL.Data.Configuration;
using CL.Data.Mapping;
using CL.Data.PostgreSql;
using CL.Data.Schema;
using CodeLogic.Core.Results;
using Data.Tests.Mapping;

namespace Data.Tests.Schema;

public sealed class SchemaDiffTests
{
    private static TableSchema Desired()
    {
        EntityAccessorRegistry.Get<TestGroup>().ValueOrThrow();   // the foreign key target must be mapped
        var map = EntityAccessorRegistry.Get<TestUser>().ValueOrThrow().Map;
        return SchemaBuilder.FromMap(map, PostgreSqlDataProvider.Instance, new DataConnectionConfig { NamingPolicy = NamingPolicy.SnakeCase }).ValueOrThrow();
    }

    [Fact]
    public void Builder_produces_columns_keys_indexes_and_foreign_keys()
    {
        var t = Desired();
        Assert.Equal(("users", "public"), (t.Name, t.Schema));
        Assert.Equal(["id"], t.PrimaryKey);
        Assert.Equal("bigint", t.Column("id")!.StoreType);                     // bigserial normalized for comparison
        Assert.True(t.Column("id")!.Identity);
        Assert.Equal("varchar(320)", t.Column("email")!.StoreType);
        Assert.True(t.Column("display_name")!.Nullable);
        Assert.False(t.Column("email")!.Nullable);
        Assert.Equal("integer", t.Column("role_code")!.StoreType);

        var composite = Assert.Single(t.Indexes, i => i.Name == "ix_users_tenant_email");
        Assert.True(composite.Unique);
        Assert.Equal(["tenant_id", "email"], composite.Columns);
        Assert.Equal([true], Assert.Single(t.Indexes, i => i.Name == "ix_users_score").Descending);

        var fk = Assert.Single(t.ForeignKeys);
        Assert.Equal(("fk_users_group_id", "test_group", "id", ForeignKeyAction.SetNull), (fk.Name, fk.ReferencedTable, fk.ReferencedColumns[0], fk.OnDelete));
    }

    [Fact]
    public void Missing_table_is_created_at_safe_and_held_at_none()
    {
        var t = Desired();
        Assert.IsType<CreateTableChange>(Assert.Single(SchemaDiff.Compare(t, null, SchemaSyncLevel.Safe).Apply));
        var none = SchemaDiff.Compare(t, null, SchemaSyncLevel.None);
        Assert.Empty(none.Apply);
        Assert.Single(none.Pending);
    }

    [Fact]
    public void Identical_tables_need_nothing()
    {
        var t = Desired();
        Assert.True(SchemaDiff.Compare(t, t, SchemaSyncLevel.Full).IsEmpty);
        // Index names may differ in the database: matched by shape.
        var renamedIndexes = t with { Indexes = t.Indexes.Select(i => i with { Name = "db_" + i.Name }).ToArray() };
        Assert.True(SchemaDiff.Compare(t, renamedIndexes, SchemaSyncLevel.Full).IsEmpty);
    }

    [Fact]
    public void Added_renamed_widened_and_narrowed_columns()
    {
        var t = Desired();
        var actual = t with
        {
            Columns = t.Columns
                .Where(c => c.Name != "external_id")                                       // missing → add
                .Select(c => c.Name switch
                {
                    "display_name" => c with { Name = "nick" },                            // old name
                    "email" => c with { StoreType = "varchar(100)" },                      // widen 100 → 320
                    "score" => c with { StoreType = "numeric(20,4)" },                     // narrow scale/precision
                    _ => c,
                })
                .Append(new ColumnSchema("legacy", "text", true))                          // extra → drop (Full)
                .ToArray(),
        };
        var desired = t with
        {
            Columns = t.Columns.Select(c => c.Name == "display_name" ? c with { PreviousName = "nick" } : c).ToArray(),
        };

        var safe = SchemaDiff.Compare(desired, actual, SchemaSyncLevel.Safe);
        Assert.Contains(safe.Apply, c => c is RenameColumnChange { From: "nick", To: "display_name" });
        Assert.Contains(safe.Apply, c => c is AddColumnChange { Column.Name: "external_id" });
        Assert.Contains(safe.Apply, c => c is AlterColumnChange { To.Name: "email" });
        Assert.Contains(safe.Pending, c => c is AlterColumnChange { To.Name: "score" });
        Assert.Contains(safe.Pending, c => c is DropColumnChange { Column: "legacy" });

        var full = SchemaDiff.Compare(desired, actual, SchemaSyncLevel.Full);
        Assert.Empty(full.Pending);
        Assert.True(full.Apply.ToList().FindIndex(c => c is RenameColumnChange) < full.Apply.ToList().FindIndex(c => c is DropColumnChange));
    }

    [Fact]
    public void Nullability_relaxing_is_safe_tightening_is_full()
    {
        var t = Desired();
        var nullableEmail = t with { Columns = t.Columns.Select(c => c.Name == "email" ? c with { Nullable = true } : c).ToArray() };
        Assert.Single(SchemaDiff.Compare(nullableEmail, t, SchemaSyncLevel.Safe).Apply);    // NOT NULL → NULL
        Assert.Single(SchemaDiff.Compare(t, nullableEmail, SchemaSyncLevel.Safe).Pending);  // NULL → NOT NULL
    }

    [Fact]
    public void Indexes_and_foreign_keys_drop_only_from_additive()
    {
        var t = Desired();
        var actual = t with
        {
            Indexes = [.. t.Indexes.Where(i => i.Name != "ix_users_score"), new IndexSchema("ix_old", false, ["email"])],
            ForeignKeys = [],
        };
        var safe = SchemaDiff.Compare(t, actual, SchemaSyncLevel.Safe);
        Assert.Contains(safe.Apply, c => c is CreateIndexChange { Index.Name: "ix_users_score" });
        Assert.Contains(safe.Apply, c => c is AddForeignKeyChange);
        Assert.Contains(safe.Pending, c => c is DropIndexChange { Name: "ix_old" });
        Assert.Contains(SchemaDiff.Compare(t, actual, SchemaSyncLevel.Additive).Apply, c => c is DropIndexChange { Name: "ix_old" });
    }

    [Theory]
    [InlineData("varchar(64)", "varchar(128)", true)]
    [InlineData("varchar(128)", "varchar(64)", false)]
    [InlineData("varchar(64)", "text", true)]
    [InlineData("nvarchar(200)", "nvarchar(max)", true)]
    [InlineData("nvarchar(max)", "nvarchar(200)", false)]
    [InlineData("decimal(10,2)", "decimal(12,2)", true)]
    [InlineData("decimal(10,2)", "decimal(10,4)", false)]
    [InlineData("decimal(10,2)", "decimal(12,4)", true)]
    [InlineData("int", "bigint", false)]
    public void Widening(string from, string to, bool expected) => Assert.Equal(expected, SchemaDiff.IsWidening(from, to));

    [Fact]
    public void Fingerprint_changes_with_the_model_only()
    {
        var t = Desired();
        Assert.Equal(SchemaBuilder.Fingerprint(t), SchemaBuilder.Fingerprint(Desired()));
        var changed = t with { Columns = t.Columns.Select(c => c.Name == "email" ? c with { StoreType = "varchar(321)" } : c).ToArray() };
        Assert.NotEqual(SchemaBuilder.Fingerprint(t), SchemaBuilder.Fingerprint(changed));
    }

    [Fact]
    public void Long_identifiers_are_cut_with_a_hash()
    {
        var name = "ix_" + new string('a', 80);
        var cut = SchemaBuilder.Identifier(name, 63);
        Assert.Equal(63, cut.Length);
        Assert.NotEqual(cut, SchemaBuilder.Identifier("ix_" + new string('a', 79) + "b", 63));
        Assert.Equal("short", SchemaBuilder.Identifier("short", 63));
    }
}
