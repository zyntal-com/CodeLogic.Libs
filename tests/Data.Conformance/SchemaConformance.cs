using CL.Data;
using CL.Data.Mapping;
using CL.Data.Schema;
using CL.Data.Sql;
using CodeLogic.Core.Results;

namespace Data.Conformance;

/// <summary>Schema parent: identity key, sized and unbounded strings, decimals, flags, guid, timestamps, indexes.</summary>
[Table(Name = "sch_parent")]
[CompositeIndex("ix_sch_parent_region_code", nameof(Region), nameof(Code), Unique = true)]
public sealed class SchemaParent
{
    /// <summary>Identity.</summary>
    [Column(Primary = true, AutoIncrement = true)] public long Id { get; set; }
    /// <summary>Code.</summary>
    [Column(Size = 40)] public string Code { get; set; } = "";
    /// <summary>Region.</summary>
    [Column(Size = 8)] public string Region { get; set; } = "";
    /// <summary>Optional note (default size).</summary>
    public string? Note { get; set; }
    /// <summary>Amount.</summary>
    [Column(Precision = 18, Scale = 2)] public decimal Amount { get; set; }
    /// <summary>Flag.</summary>
    public bool Enabled { get; set; }
    /// <summary>Reference.</summary>
    public Guid Ref { get; set; }
    /// <summary>Timestamp, indexed descending.</summary>
    [Index(Name = "ix_sch_parent_seen", Direction = SortOrder.Descending)] public DateTimeOffset Seen { get; set; }
    /// <summary>Optional counter.</summary>
    public int? Count { get; set; }
    /// <summary>Whole-second timestamp (MySQL <c>DATETIME</c>), covered by an index with an included column.</summary>
    [Column(Precision = 0)]
    [Index(Name = "ix_sch_parent_stamp", Include = [nameof(Amount)])]
    public DateTime Stamp { get; set; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
}

/// <summary>Schema child: composite key and a cascading foreign key.</summary>
[Table(Name = "sch_child")]
public sealed class SchemaChild
{
    /// <summary>Parent (key part 1).</summary>
    [Column(Primary = true, KeyOrder = 1)]
    [ForeignKey<SchemaParent>(nameof(SchemaParent.Id), OnDelete = ForeignKeyAction.Cascade)]
    public long ParentId { get; set; }
    /// <summary>Line (key part 2).</summary>
    [Column(Primary = true, KeyOrder = 2)] public int Line { get; set; }
    /// <summary>Text.</summary>
    [Column(Size = 100)] public string Text { get; set; } = "";
}

/// <summary>
/// Schema round trip: DDL from <see cref="DdlGenerator"/> creates the tables, the introspector reads them back,
/// and the diff against the model is empty.
/// </summary>
public abstract class SchemaConformance(ProviderFixture fixture) : ConformanceSuite(fixture)
{
    private async Task<(TableSchema Parent, TableSchema Child)> CreateAsync()
    {
        var provider = Db.Provider;
        (await Db.ExecuteSqlAsync($"DROP TABLE IF EXISTS {Sql.Identifier("sch_child")}")).ValueOrThrow();
        (await Db.ExecuteSqlAsync($"DROP TABLE IF EXISTS {Sql.Identifier("sch_parent")}")).ValueOrThrow();

        var parentMap = EntityAccessorRegistry.Get<SchemaParent>().ValueOrThrow().Map;
        var childMap = EntityAccessorRegistry.Get<SchemaChild>().ValueOrThrow().Map;
        var parent = SchemaBuilder.FromMap(parentMap, provider, Db.Config).ValueOrThrow();
        var child = SchemaBuilder.FromMap(childMap, provider, Db.Config).ValueOrThrow();
        foreach (var table in new[] { parent, child })
        foreach (var statement in provider.Ddl.Generate(new CreateTableChange(table)))
            (await Db.ExecuteSqlAsync(SqlText.Raw(statement))).ValueOrThrow();
        return (parent, child);
    }

    private static void AssertSame(TableSchema desired, TableSchema? actual)
    {
        Assert.NotNull(actual);
        var plan = SchemaDiff.Compare(desired, actual, SchemaSyncLevel.Full);
        Assert.True(plan.IsEmpty, "Unexpected differences: " + string.Join("; ", plan.Apply.Select(c => c.Describe())));
    }

    [SkippableFact]
    public async Task Created_tables_read_back_without_differences()
    {
        RequireProvider();
        var (parent, child) = await CreateAsync();
        var introspector = Db.Provider.Introspector!;

        var parentActual = (await introspector.ReadTableAsync(Db, "sch_parent")).ValueOrThrow();
        AssertSame(parent, parentActual);
        Assert.Equal(["Id"], parentActual!.PrimaryKey);
        Assert.True(parentActual.Column("Id")!.Identity);
        Assert.True(parentActual.Column("Note")!.Nullable);
        Assert.False(parentActual.Column("Code")!.Nullable);
        Assert.Contains(parentActual.Indexes, i => i.Unique && i.Columns.SequenceEqual(["Region", "Code"]));

        var childActual = (await introspector.ReadTableAsync(Db, "sch_child")).ValueOrThrow();
        AssertSame(child, childActual);
        Assert.Equal(["ParentId", "Line"], childActual!.PrimaryKey);
        var fk = Assert.Single(childActual.ForeignKeys);
        Assert.Equal(("sch_parent", ForeignKeyAction.Cascade), (fk.ReferencedTable, fk.OnDelete));
        Assert.Equal("fk_sch_child_ParentId", fk.Name);
    }

    [SkippableFact]
    public async Task Missing_tables_and_listing()
    {
        RequireProvider();
        await CreateAsync();
        var introspector = Db.Provider.Introspector!;
        Assert.Null((await introspector.ReadTableAsync(Db, "sch_does_not_exist")).ValueOrThrow());
        var tables = (await introspector.ListTablesAsync(Db)).ValueOrThrow();
        Assert.Contains("sch_parent", tables);
        Assert.Contains("sch_child", tables);
        Assert.Contains(ConformanceTables.Products, tables);
    }

    [SkippableFact]
    public async Task Changes_apply_and_read_back()
    {
        RequireProvider();
        var (parent, _) = await CreateAsync();
        var provider = Db.Provider;
        (await Db.ExecuteSqlAsync($"INSERT INTO {Sql.Identifier("sch_parent")} ({Sql.Identifier("Code")}, {Sql.Identifier("Region")}, {Sql.Identifier("Amount")}, {Sql.Identifier("Enabled")}, {Sql.Identifier("Ref")}, {Sql.Identifier("Seen")}, {Sql.Identifier("Stamp")}) VALUES ({"A"}, {"eu"}, {1.5m}, {true}, {Guid.NewGuid()}, {DateTimeOffset.UtcNow}, {new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)})")).ValueOrThrow();

        // Widen Code, add a NOT NULL column (existing rows need the implicit default), rename Note, drop Count, add an index.
        var desired = parent with
        {
            Columns =
            [
                .. parent.Columns
                    .Where(c => c.Name != "Count")
                    .Select(c => c.Name switch
                    {
                        "Code" => c with { StoreType = provider.NormalizeStoreType(WiderString(c.StoreType)) },
                        "Note" => c with { Name = "Remark", PreviousName = "Note" },
                        _ => c,
                    }),
                new ColumnSchema("Qty", parent.Column("Count")!.StoreType, false),
            ],
            Indexes = [.. parent.Indexes, new IndexSchema("ix_sch_parent_amount", false, ["Amount"])],
        };
        var introspector = provider.Introspector!;
        var before = (await introspector.ReadTableAsync(Db, "sch_parent")).ValueOrThrow();
        var plan = SchemaDiff.Compare(desired, before, SchemaSyncLevel.Full);
        Assert.Empty(plan.Pending);
        foreach (var change in plan.Apply)
        foreach (var statement in provider.Ddl.Generate(change))
            (await Db.ExecuteSqlAsync(SqlText.Raw(statement))).ValueOrThrow();

        AssertSame(desired with { Columns = desired.Columns.Select(c => c with { PreviousName = null }).ToArray() },
            (await introspector.ReadTableAsync(Db, "sch_parent")).ValueOrThrow());
        var code = await Db.SqlScalarAsync<string>($"SELECT {Sql.Identifier("Code")} FROM {Sql.Identifier("sch_parent")}");
        Assert.Equal("A", code.ValueOrThrow());   // rows survive (SQLite rebuilds the table)
    }

    private static string WiderString(string storeType)
    {
        var open = storeType.IndexOf('(');
        return open < 0 ? storeType : storeType[..open] + "(80)";
    }
}
