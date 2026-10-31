using System.Text.RegularExpressions;

namespace CL.Data.Schema;

/// <summary>The changes table sync may apply now, and the ones the sync level holds back (drift).</summary>
/// <param name="Apply">Changes to apply, in order.</param>
/// <param name="Pending">Changes the sync level does not allow; reported as drift.</param>
public sealed record SchemaPlan(IReadOnlyList<SchemaChange> Apply, IReadOnlyList<SchemaChange> Pending)
{
    /// <summary>True when the table already matches the model.</summary>
    public bool IsEmpty => Apply.Count == 0 && Pending.Count == 0;
}

/// <summary>
/// Compares the desired table with the database's and plans the changes, sorted by what each
/// <see cref="SchemaSyncLevel"/> allows:
/// <list type="bullet">
/// <item><b>Safe:</b> create the table, add columns, rename columns (<c>PreviousName</c>), widen types (a larger size,
/// NOT NULL to NULL), add indexes and foreign keys.</item>
/// <item><b>Additive:</b> Safe, plus dropping indexes and foreign keys that are no longer declared.</item>
/// <item><b>Full:</b> Additive, plus dropping columns and narrowing types or nullability.</item>
/// </list>
/// </summary>
public static partial class SchemaDiff
{
    /// <summary>Plans the changes from <paramref name="actual"/> (null when the table does not exist) to <paramref name="desired"/>.</summary>
    public static SchemaPlan Compare(TableSchema desired, TableSchema? actual, SchemaSyncLevel level)
    {
        List<SchemaChange> apply = [];
        List<SchemaChange> pending = [];
        void Plan(SchemaChange change, SchemaSyncLevel needs) => (level >= needs ? apply : pending).Add(change);

        if (actual is null)
        {
            Plan(new CreateTableChange(desired), SchemaSyncLevel.Safe);
            return new SchemaPlan(apply, pending);
        }

        var table = desired.Name;
        var schema = desired.Schema;
        HashSet<string> matched = [with(StringComparer.OrdinalIgnoreCase)];

        // Columns: renames, additions, type and nullability changes.
        foreach (var column in desired.Columns)
        {
            var current = actual.Column(column.Name);
            if (current is null && column.PreviousName is { } previous && actual.Column(previous) is { } old)
            {
                Plan(new RenameColumnChange(table, schema, old.Name, column.Name), SchemaSyncLevel.Safe);
                current = old with { Name = column.Name };
                matched.Add(old.Name);
            }

            if (current is null)
            {
                Plan(new AddColumnChange(table, schema, column), SchemaSyncLevel.Safe);
                continue;
            }

            matched.Add(current.Name);
            if (SameType(current.StoreType, column.StoreType) && current.Nullable == column.Nullable) continue;
            var widening = (SameType(current.StoreType, column.StoreType) || IsWidening(current.StoreType, column.StoreType))
                           && (column.Nullable || !current.Nullable);
            Plan(new AlterColumnChange(table, schema, current, column, desired), widening ? SchemaSyncLevel.Safe : SchemaSyncLevel.Full);
        }

        foreach (var column in actual.Columns.Where(c => !matched.Contains(c.Name)))
            Plan(new DropColumnChange(table, schema, column.Name, desired), SchemaSyncLevel.Full);

        // Indexes are matched by shape, not name (databases name some indexes themselves).
        foreach (var index in desired.Indexes.Where(i => !actual.Indexes.Any(a => a.SameShape(i))))
            Plan(new CreateIndexChange(table, schema, index), SchemaSyncLevel.Safe);
        foreach (var index in actual.Indexes.Where(a => !desired.Indexes.Any(i => i.SameShape(a))))
        {
            // Keep indexes that back a foreign key (MySQL creates them and refuses to drop them while the key exists).
            var backsForeignKey = desired.ForeignKeys.Concat(actual.ForeignKeys)
                .Any(f => index.Columns.Count >= f.Columns.Count && index.Columns.Take(f.Columns.Count).SequenceEqual(f.Columns, StringComparer.OrdinalIgnoreCase));
            var isPrimary = index.Columns.SequenceEqual(actual.PrimaryKey, StringComparer.OrdinalIgnoreCase) && index.Unique;
            if (!backsForeignKey && !isPrimary) Plan(new DropIndexChange(table, schema, index.Name), SchemaSyncLevel.Additive);
        }

        foreach (var fk in desired.ForeignKeys.Where(f => !actual.ForeignKeys.Any(a => a.SameShape(f))))
            Plan(new AddForeignKeyChange(table, schema, fk, desired), SchemaSyncLevel.Safe);
        foreach (var fk in actual.ForeignKeys.Where(a => !desired.ForeignKeys.Any(f => f.SameShape(a))))
            Plan(new DropForeignKeyChange(table, schema, fk.Name, desired), SchemaSyncLevel.Additive);

        // Drop foreign keys before the columns and indexes they use; add them after.
        return new SchemaPlan(Order(apply), Order(pending));
    }

    private static IReadOnlyList<SchemaChange> Order(List<SchemaChange> changes) =>
        changes.OrderBy(c => c switch
        {
            DropForeignKeyChange => 0,
            DropIndexChange => 1,
            CreateTableChange => 2,
            RenameColumnChange => 3,
            AddColumnChange => 4,
            AlterColumnChange => 5,
            DropColumnChange => 6,
            CreateIndexChange => 7,
            AddForeignKeyChange => 8,
        }).ToArray();

    private static bool SameType(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="to"/> holds every value of <paramref name="from"/>: the same base type with a larger size
    /// or precision, or a bounded text/binary type moving to its unbounded form.
    /// </summary>
    internal static bool IsWidening(string from, string to)
    {
        var f = TypePattern().Match(from);
        var t = TypePattern().Match(to);
        if (!f.Success) return false;

        var baseFrom = f.Groups["base"].Value;
        if (Unbounded.TryGetValue(baseFrom, out var unbounded) && unbounded.Contains(to, StringComparer.OrdinalIgnoreCase)) return true;
        if (!t.Success || !string.Equals(baseFrom, t.Groups["base"].Value, StringComparison.OrdinalIgnoreCase)) return false;
        if (!f.Groups["size"].Success || !t.Groups["size"].Success) return false;
        if (t.Groups["size"].Value.Equals("max", StringComparison.OrdinalIgnoreCase)) return true;
        if (f.Groups["size"].Value.Equals("max", StringComparison.OrdinalIgnoreCase)) return false;

        var sizeFrom = int.Parse(f.Groups["size"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var sizeTo = int.Parse(t.Groups["size"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var scaleFrom = f.Groups["scale"].Success ? int.Parse(f.Groups["scale"].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        var scaleTo = t.Groups["scale"].Success ? int.Parse(t.Groups["scale"].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        // A decimal widens when both its integer digits and its scale do not shrink.
        return sizeTo - scaleTo >= sizeFrom - scaleFrom && scaleTo >= scaleFrom && sizeTo >= sizeFrom;
    }

    private static readonly Dictionary<string, string[]> Unbounded = new(StringComparer.OrdinalIgnoreCase)
    {
        ["varchar"] = ["text", "longtext", "mediumtext", "varchar(max)"],
        ["nvarchar"] = ["nvarchar(max)"],
        ["char"] = ["text", "longtext"],
        ["varbinary"] = ["varbinary(max)", "longblob", "bytea"],
    };

    [GeneratedRegex(@"^(?<base>[a-z][a-z0-9_]*(?: [a-z][a-z0-9_]*)*)\s*(?:\((?<size>\d+|max)(?:\s*,\s*(?<scale>\d+))?\))?\s*(?<rest>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex TypePattern();
}
