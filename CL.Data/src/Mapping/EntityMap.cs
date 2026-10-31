using System.Collections.Frozen;

namespace CL.Data.Mapping;

/// <summary>The primary key: one column, several (composite, in key order), or none.</summary>
public sealed class KeyMap
{
    internal KeyMap(IReadOnlyList<ColumnMap> columns) => Columns = columns;

    /// <summary>Key columns in key order.</summary>
    public IReadOnlyList<ColumnMap> Columns { get; }

    /// <summary>True when the entity has no primary key (it can be read and inserted, not updated by key).</summary>
    public bool IsEmpty => Columns.Count == 0;

    /// <summary>True for a key of more than one column.</summary>
    public bool IsComposite => Columns.Count > 1;
}

/// <summary>A column in an index.</summary>
/// <param name="Column">Indexed column.</param>
/// <param name="Direction">Sort direction.</param>
public readonly record struct IndexColumn(ColumnMap Column, SortOrder Direction);

/// <summary>An index of an entity.</summary>
/// <param name="Name">Declared name; null lets the provider generate one.</param>
/// <param name="Unique">Unique index.</param>
/// <param name="Columns">Key columns in order.</param>
/// <param name="Include">Covering columns.</param>
public sealed record IndexMap(string? Name, bool Unique, IReadOnlyList<IndexColumn> Columns, IReadOnlyList<ColumnMap> Include);

/// <summary>A foreign key of an entity.</summary>
/// <param name="Column">Referencing column.</param>
/// <param name="ReferencedEntity">Referenced entity type, when declared by type.</param>
/// <param name="ReferencedTable">Referenced table name, when declared by name.</param>
/// <param name="ReferencedColumn">Referenced column (or property, with <paramref name="ReferencedEntity"/>).</param>
/// <param name="OnDelete">Action on delete.</param>
/// <param name="OnUpdate">Action on update.</param>
/// <param name="ConstraintName">Declared constraint name.</param>
public sealed record ForeignKeyMap(ColumnMap Column, Type? ReferencedEntity, string? ReferencedTable, string ReferencedColumn,
    ForeignKeyAction OnDelete, ForeignKeyAction OnUpdate, string? ConstraintName);

/// <summary>Database names of an entity under one naming policy.</summary>
public sealed class EntityNames
{
    internal EntityNames(string table, string[] columns, FrozenDictionary<string, ColumnMap> byColumn)
    {
        Table = table;
        Columns = columns;
        ByColumn = byColumn;
    }

    /// <summary>Table name.</summary>
    public string Table { get; }

    /// <summary>Column names, indexed like <see cref="EntityMap.Columns"/>.</summary>
    public IReadOnlyList<string> Columns { get; }

    /// <summary>Columns by database name (case-insensitive).</summary>
    public FrozenDictionary<string, ColumnMap> ByColumn { get; }

    /// <summary>Database name of <paramref name="column"/>.</summary>
    public string Of(ColumnMap column) => Columns[column.Ordinal];
}

/// <summary>Input for a multi-column index declared on the class (<c>[CompositeIndex]</c>).</summary>
/// <param name="Name">Index name.</param>
/// <param name="Properties">Property names in order.</param>
/// <param name="Unique">Unique index.</param>
/// <param name="Include">Covering property names.</param>
public sealed record CompositeIndexDefinition(string Name, IReadOnlyList<string> Properties, bool Unique, IReadOnlyList<string>? Include);

/// <summary>
/// Everything CL.Data knows about a mapped type: table, columns, key, roles, indexes and foreign keys.
/// Created once per type by the generated accessor (or the reflection fallback) through <see cref="Create"/>,
/// which validates the model; immutable afterwards.
/// </summary>
public sealed class EntityMap
{
    private readonly FrozenDictionary<string, ColumnMap> _byProperty;
    private readonly EntityNames?[] _names = new EntityNames?[3];   // one per NamingPolicy

    private EntityMap(Type clrType, string? tableName, string? schema, string? comment, bool isTable,
        ColumnMap[] columns, IReadOnlyList<Attribute> tableHints)
    {
        ClrType = clrType;
        ExplicitTableName = tableName;
        Schema = schema;
        Comment = comment;
        IsTable = isTable;
        Columns = columns;
        TableHints = tableHints;
        _byProperty = columns.ToFrozenDictionary(c => c.PropertyName, StringComparer.Ordinal);

        Key = new KeyMap(columns.Where(c => c.Has(ColumnRoles.Key))
            .OrderBy(c => c.KeyOrder).ThenBy(c => c.Ordinal).ToArray());
        Identity = Single(ColumnRoles.Identity);
        SoftDelete = Single(ColumnRoles.SoftDelete);
        ConcurrencyToken = Single(ColumnRoles.ConcurrencyToken);
        CreatedAt = Single(ColumnRoles.CreatedAt);
        UpdatedAt = Single(ColumnRoles.UpdatedAt);
        CreatedBy = Single(ColumnRoles.CreatedBy);
        UpdatedBy = Single(ColumnRoles.UpdatedBy);
        TenantKey = Single(ColumnRoles.TenantKey);
        Retention = Single(ColumnRoles.Retention);

        ColumnMap? Single(ColumnRoles role) => columns.FirstOrDefault(c => c.Has(role));
    }

    /// <summary>The mapped CLR type.</summary>
    public Type ClrType { get; }

    /// <summary>Explicit table name; null derives it from the type name.</summary>
    public string? ExplicitTableName { get; }

    /// <summary>Schema, when declared.</summary>
    public string? Schema { get; }

    /// <summary>Table comment.</summary>
    public string? Comment { get; }

    /// <summary>True for <c>[Table]</c> types; false for <c>[DataRow]</c> types (read-only shapes).</summary>
    public bool IsTable { get; }

    /// <summary>Mapped columns in declaration order.</summary>
    public IReadOnlyList<ColumnMap> Columns { get; }

    /// <summary>Primary key.</summary>
    public KeyMap Key { get; }

    /// <summary>Identity column.</summary>
    public ColumnMap? Identity { get; }

    /// <summary>Soft-delete column.</summary>
    public ColumnMap? SoftDelete { get; }

    /// <summary>Concurrency token column.</summary>
    public ColumnMap? ConcurrencyToken { get; }

    /// <summary>Created-at column.</summary>
    public ColumnMap? CreatedAt { get; }

    /// <summary>Updated-at column.</summary>
    public ColumnMap? UpdatedAt { get; }

    /// <summary>Created-by column.</summary>
    public ColumnMap? CreatedBy { get; }

    /// <summary>Updated-by column.</summary>
    public ColumnMap? UpdatedBy { get; }

    /// <summary>Tenant key column.</summary>
    public ColumnMap? TenantKey { get; }

    /// <summary>Retention timestamp column.</summary>
    public ColumnMap? Retention { get; }

    /// <summary>Indexes (single-column ones from <c>[Column]</c>, <c>[Index]</c> groups and <c>[CompositeIndex]</c>).</summary>
    public IReadOnlyList<IndexMap> Indexes { get; private set; } = [];

    /// <summary>Foreign keys.</summary>
    public IReadOnlyList<ForeignKeyMap> ForeignKeys { get; private set; } = [];

    /// <summary>Provider hint attributes on the type.</summary>
    public IReadOnlyList<Attribute> TableHints { get; }

    /// <summary>Column for <paramref name="propertyName"/>, or null.</summary>
    public ColumnMap? FindProperty(string propertyName) => _byProperty.GetValueOrDefault(propertyName);

    /// <summary>Column for <paramref name="propertyName"/>; throws when it is not mapped.</summary>
    public ColumnMap Property(string propertyName) => FindProperty(propertyName)
        ?? throw new InvalidOperationException($"'{ClrType.Name}.{propertyName}' is not a mapped column.");

    /// <summary>Returns the provider hint of type <typeparamref name="THint"/> on the type, if any.</summary>
    public THint? Hint<THint>() where THint : Attribute
    {
        foreach (var hint in TableHints)
            if (hint is THint typed) return typed;
        return null;
    }

    /// <summary>Database names under <paramref name="policy"/>. Computed once per policy.</summary>
    public EntityNames GetNames(NamingPolicy policy)
    {
        var index = (int)policy;
        return _names[index] ??= Build();

        EntityNames Build()
        {
            var table = ExplicitTableName ?? NameConverter.Convert(ClrType.Name, policy);
            var names = new string[Columns.Count];
            for (var i = 0; i < names.Length; i++)
                names[i] = Columns[i].ExplicitName ?? NameConverter.Convert(Columns[i].PropertyName, policy);
            var byColumn = Columns.ToFrozenDictionary(c => names[c.Ordinal], StringComparer.OrdinalIgnoreCase);
            return new EntityNames(table, names, byColumn);
        }
    }

    /// <summary>
    /// Creates and validates a map. Invalid models (duplicate columns, a composite key without
    /// <c>KeyOrder</c>, a role on a property of the wrong type, ...) throw <see cref="InvalidOperationException"/>
    /// listing every problem; the source generator reports the same problems at compile time.
    /// </summary>
    /// <param name="clrType">The mapped type.</param>
    /// <param name="table">The <c>[Table]</c> attribute; null for a <c>[DataRow]</c> type.</param>
    /// <param name="columns">Columns in declaration order.</param>
    /// <param name="compositeIndexes">Class-level indexes.</param>
    /// <param name="tableHints">Provider hint attributes on the type.</param>
    public static EntityMap Create(Type clrType, TableAttribute? table, IEnumerable<ColumnMap> columns,
        IEnumerable<CompositeIndexDefinition>? compositeIndexes = null, IEnumerable<Attribute>? tableHints = null)
    {
        ArgumentNullException.ThrowIfNull(clrType);
        ArgumentNullException.ThrowIfNull(columns);

        var cols = columns.ToArray();
        List<string> problems = [];
        var owner = clrType.Name;

        for (var i = 0; i < cols.Length; i++) cols[i].Ordinal = i;

        if (table is not null && cols.Length == 0)
            problems.Add($"'{owner}' maps no columns.");

        foreach (var group in cols.GroupBy(c => c.PropertyName, StringComparer.Ordinal).Where(g => g.Count() > 1))
            problems.Add($"Property '{group.Key}' is mapped twice.");
        foreach (var group in cols.Where(c => c.ExplicitName is not null)
                     .GroupBy(c => c.ExplicitName!, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add($"Column name '{group.Key}' is used by {string.Join(", ", group.Select(c => c.PropertyName))}.");
        foreach (var name in cols.Select(c => c.ExplicitName).Append(table?.Name).OfType<string>())
            if (!IsSafeIdentifier(name))
                problems.Add($"Name '{name}' is empty or contains a quote, NUL or line break.");

        // Keys.
        var keys = cols.Where(c => c.Has(ColumnRoles.Key)).ToArray();
        if (keys.Length > 1)
        {
            if (keys.Any(k => k.KeyOrder <= 0))
                problems.Add($"Composite key of '{owner}' needs KeyOrder on every key column ({string.Join(", ", keys.Select(k => k.PropertyName))}).");
            else if (keys.Select(k => k.KeyOrder).Distinct().Count() != keys.Length)
                problems.Add($"Composite key of '{owner}' repeats a KeyOrder.");
        }

        foreach (var key in keys.Where(k => k.IsNullable))
            problems.Add($"Key column '{key.PropertyName}' cannot be nullable.");

        // Roles: at most one column each, and the right type.
        foreach (var role in Enum.GetValues<ColumnRoles>().Where(r => r is not ColumnRoles.None and not ColumnRoles.Key))
        {
            var withRole = cols.Where(c => c.Has(role)).ToArray();
            if (withRole.Length > 1)
                problems.Add($"'{owner}' has more than one {role} column ({string.Join(", ", withRole.Select(c => c.PropertyName))}).");
            foreach (var column in withRole)
                if (RoleTypeProblem(role, column) is { } problem)
                    problems.Add(problem);
        }

        // Indexes.
        List<IndexMap> indexes = [];
        foreach (var c in cols)
        {
            if (c.Unique) indexes.Add(new IndexMap(null, true, [new IndexColumn(c, SortOrder.Ascending)], []));
            else if (c.Indexed) indexes.Add(new IndexMap(null, false, [new IndexColumn(c, SortOrder.Ascending)], []));
        }

        var declared = cols.SelectMany(c => c.IndexAttributes.Select(a => (Column: c, Attribute: a))).ToArray();
        foreach (var single in declared.Where(d => d.Attribute.Name is null))
            indexes.Add(new IndexMap(null, single.Attribute.Unique,
                [new IndexColumn(single.Column, single.Attribute.Direction)],
                ResolveProperties(single.Attribute.Include, $"index on {single.Column.PropertyName}")));
        foreach (var group in declared.Where(d => d.Attribute.Name is not null)
                     .GroupBy(d => d.Attribute.Name!, StringComparer.OrdinalIgnoreCase))
        {
            var parts = group.OrderBy(d => d.Attribute.Order).ThenBy(d => d.Column.Ordinal).ToArray();
            if (parts.Length > 1 && parts.Select(p => p.Attribute.Order).Distinct().Count() != parts.Length)
                problems.Add($"Index '{group.Key}' needs a distinct Order on each of its columns.");
            if (parts.Select(p => p.Attribute.Unique).Distinct().Count() > 1)
                problems.Add($"Index '{group.Key}' is declared both unique and non-unique.");
            indexes.Add(new IndexMap(group.Key, parts[0].Attribute.Unique,
                parts.Select(p => new IndexColumn(p.Column, p.Attribute.Direction)).ToArray(),
                ResolveProperties(parts.SelectMany(p => p.Attribute.Include ?? []).Distinct().ToArray(), $"index '{group.Key}'")));
        }

        foreach (var composite in compositeIndexes ?? [])
        {
            if (composite.Properties.Count == 0)
            {
                problems.Add($"Composite index '{composite.Name}' lists no properties.");
                continue;
            }
            indexes.Add(new IndexMap(composite.Name, composite.Unique,
                ResolveProperties(composite.Properties, $"composite index '{composite.Name}'")
                    .Select(c => new IndexColumn(c, SortOrder.Ascending)).ToArray(),
                ResolveProperties(composite.Include, $"composite index '{composite.Name}'")));
        }

        foreach (var group in indexes.Where(i => i.Name is not null).GroupBy(i => i.Name!, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
            problems.Add($"Index name '{group.Key}' is declared more than once.");

        // Foreign keys.
        var foreignKeys = cols.Where(c => c.ForeignKey is not null)
            .Select(c => new ForeignKeyMap(c, c.ForeignKey!.ReferencedEntity, c.ForeignKey.ReferencedTable,
                c.ForeignKey.ReferencedColumn, c.ForeignKey.OnDelete, c.ForeignKey.OnUpdate, c.ForeignKey.ConstraintName))
            .ToArray();
        foreach (var fk in foreignKeys.Where(f => f.OnDelete == ForeignKeyAction.SetNull || f.OnUpdate == ForeignKeyAction.SetNull))
            if (!fk.Column.IsNullable)
                problems.Add($"Foreign key on '{fk.Column.PropertyName}' uses SET NULL but the column is not nullable.");

        if (problems.Count > 0)
            throw new InvalidOperationException(
                $"Invalid mapping for '{clrType.FullName}':{Environment.NewLine} - " +
                string.Join(Environment.NewLine + " - ", problems));

        return new EntityMap(clrType, table?.Name, table?.Schema, table?.Comment, table is not null, cols,
            tableHints?.ToArray() ?? [])
        {
            Indexes = indexes,
            ForeignKeys = foreignKeys,
        };

        ColumnMap[] ResolveProperties(IReadOnlyList<string>? names, string where)
        {
            if (names is null || names.Count == 0) return [];
            List<ColumnMap> resolved = [];
            foreach (var n in names)
            {
                var match = cols.FirstOrDefault(c => c.PropertyName == n)
                            ?? cols.FirstOrDefault(c => string.Equals(c.ExplicitName, n, StringComparison.OrdinalIgnoreCase));
                if (match is null) problems.Add($"'{n}' in {where} is not a mapped property.");
                else resolved.Add(match);
            }
            return resolved.ToArray();
        }
    }

    private static string? RoleTypeProblem(ColumnRoles role, ColumnMap column)
    {
        var type = column.ValueType;
        var isTime = type == typeof(DateTimeOffset) || type == typeof(DateTime);
        return role switch
        {
            ColumnRoles.Identity when !IsInteger(type) =>
                $"AutoIncrement column '{column.PropertyName}' must be an integer type.",
            ColumnRoles.SoftDelete when !isTime || !column.IsNullable =>
                $"[SoftDelete] column '{column.PropertyName}' must be DateTimeOffset? or DateTime?.",
            ColumnRoles.CreatedAt or ColumnRoles.UpdatedAt or ColumnRoles.Retention when !isTime =>
                $"{role} column '{column.PropertyName}' must be DateTimeOffset or DateTime.",
            ColumnRoles.ConcurrencyToken when type != typeof(int) && type != typeof(long) && type != typeof(byte[]) =>
                $"[ConcurrencyToken] column '{column.PropertyName}' must be int, long or byte[].",
            ColumnRoles.Retention when column.RetainDays <= 0 =>
                $"[RetainDays] on '{column.PropertyName}' must keep at least one day.",
            _ => null,
        };
    }

    internal static bool IsInteger(Type type) =>
        type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) ||
        type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) || type == typeof(sbyte);

    private static bool IsSafeIdentifier(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.AsSpan().IndexOfAny("`\"[]\0\r\n") < 0;

    /// <inheritdoc />
    public override string ToString() => ExplicitTableName ?? ClrType.Name;
}
