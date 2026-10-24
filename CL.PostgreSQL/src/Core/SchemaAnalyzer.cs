using System.Reflection;
using System.Text;
using CL.PostgreSQL.Models;
using CodeLogic.Core.Logging;
using Npgsql;

namespace CL.PostgreSQL.Core;

/// <summary>
/// Analyzes entity types and generates the DDL statements required to create or alter
/// the corresponding PostgreSQL table so it matches the current class definition.
/// </summary>
internal sealed class SchemaAnalyzer
{
    private readonly ILogger? _logger;

    public SchemaAnalyzer(ILogger? logger = null)
    {
        _logger = logger;
    }

    // ── Table name ────────────────────────────────────────────────────────────

    /// <summary>Returns the table name for the given entity type.</summary>
    public static string GetTableName(Type entityType)
    {
        var attr = entityType.GetCustomAttribute<TableAttribute>();
        return !string.IsNullOrEmpty(attr?.Name) ? attr.Name! : entityType.Name;
    }

    /// <summary>
    /// The schema an entity maps to: <see cref="TableAttribute.Schema"/> when set, otherwise
    /// the <c>DefaultSchema</c> configured for <paramref name="connectionId"/> (<c>public</c>
    /// unless the connection changed it).
    /// </summary>
    public static string GetSchemaName(Type entityType, string? connectionId = null)
    {
        var attr = entityType.GetCustomAttribute<TableAttribute>();
        return !string.IsNullOrEmpty(attr?.Schema)
            ? attr.Schema!
            : PostgreSqlRuntimeOptions.DefaultSchemaFor(connectionId);
    }

    /// <summary>The quoted, schema-qualified table reference for an entity type.</summary>
    public static string GetQualifiedName(Type entityType, string? connectionId = null) =>
        PostgreSqlDialect.Qualify(GetSchemaName(entityType, connectionId), GetTableName(entityType));


    // ── CREATE TABLE ──────────────────────────────────────────────────────────

    /// <summary>
    /// Generates a CREATE TABLE IF NOT EXISTS statement for the given entity type.
    /// </summary>
    public string GenerateCreateTable(Type entityType, string? connectionId = null)
    {
        var options = PostgreSqlRuntimeOptions.For(connectionId);
        var defaultStringSize = options.DefaultStringSize;
        var tableAttr = entityType.GetCustomAttribute<TableAttribute>() ?? new TableAttribute();
        var tableName = !string.IsNullOrEmpty(tableAttr.Name) ? tableAttr.Name! : entityType.Name;
        var schemaName = !string.IsNullOrEmpty(tableAttr.Schema) ? tableAttr.Schema! : options.DefaultSchema;
        var qualifiedTable = PostgreSqlDialect.Qualify(schemaName, tableName);

        var sql = new StringBuilder();
        // Create the schema first. Sync already creates tables, indexes, constraints and
        // triggers, so requiring the schema to exist beforehand would be the one gap in an
        // otherwise declarative story -- and CL.MSSQL already does this for its schemas.
        // IF NOT EXISTS keeps it idempotent and costs nothing on the common path.
        if (!string.Equals(schemaName, PostgreSqlDialect.DefaultSchema, StringComparison.Ordinal))
            sql.AppendLine($"CREATE SCHEMA IF NOT EXISTS {PostgreSqlDialect.Quote(schemaName)};");
        sql.AppendLine($"CREATE TABLE IF NOT EXISTS {qualifiedTable} (");

        var columns = new List<string>();
        var primaryKeys = new List<string>();
        var indexes = new List<string>();
        var uniqueIndexes = new List<string>();
        var foreignKeys = new List<string>();

        var properties = GetMappedProperties(entityType);

        foreach (var prop in properties)
        {
            var colAttr = prop.GetCustomAttribute<ColumnAttribute>();
            var colName = !string.IsNullOrEmpty(colAttr?.Name) ? colAttr.Name! : prop.Name;
            var colDef = BuildColumnDef(prop, colAttr, colName, defaultStringSize);
            columns.Add($"  {colDef}");

            if (colAttr?.Primary == true)
                primaryKeys.Add($"{PostgreSqlDialect.Quote(colName)}");

            // PostgreSQL has no inline INDEX clause inside CREATE TABLE: indexes are
            // separate statements. UNIQUE, by contrast, is a genuine table constraint.
            if (colAttr?.Index == true && colAttr.Primary == false && colAttr.Unique == false)
                indexes.Add($"CREATE INDEX IF NOT EXISTS {PostgreSqlDialect.Quote($"idx_{tableName}_{colName}")} " +
                            $"ON {qualifiedTable} ({PostgreSqlDialect.Quote(colName)});");

            if (colAttr?.Unique == true && colAttr.Primary == false)
                uniqueIndexes.Add($"  CONSTRAINT {PostgreSqlDialect.Quote($"uq_{tableName}_{colName}")} " +
                                  $"UNIQUE ({PostgreSqlDialect.Quote(colName)})");

            // [Index] attributes — modern form supporting covering Include columns.
            foreach (var idxAttr in prop.GetCustomAttributes<IndexAttribute>())
            {
                var idxName = !string.IsNullOrEmpty(idxAttr.Name)
                    ? idxAttr.Name!
                    : (idxAttr.Unique ? $"uq_{tableName}_{colName}" : $"idx_{tableName}_{colName}");
                var includeCols = (idxAttr.Include ?? Array.Empty<string>())
                    .Select(propName => ResolveColumnName(entityType, propName))
                    .ToArray();
                // Include columns become a real covering-index INCLUDE clause (PostgreSQL 11+),
                // which stores them in the leaf pages without making them part of the key.
                var includeClause = includeCols.Length == 0
                    ? string.Empty
                    : $" INCLUDE ({string.Join(", ", includeCols.Select(PostgreSqlDialect.Quote))})";
                var keyword = idxAttr.Unique ? "CREATE UNIQUE INDEX" : "CREATE INDEX";
                indexes.Add($"{keyword} IF NOT EXISTS {PostgreSqlDialect.Quote(idxName)} " +
                            $"ON {qualifiedTable} ({PostgreSqlDialect.Quote(colName)}){includeClause};");
            }

            // Foreign key
            var fkAttr = prop.GetCustomAttribute<ForeignKeyAttribute>();
            if (fkAttr is not null)
            {
                var constraintName = fkAttr.ConstraintName
                    ?? $"fk_{tableName}_{colName}_{fkAttr.ReferenceTable}";
                var onDelete = FkActionToSql(fkAttr.OnDelete);
                var onUpdate = FkActionToSql(fkAttr.OnUpdate);
                // The referenced table is schema-qualified: an unqualified name would resolve
                // against search_path at DDL time and could bind to the wrong table.
                foreignKeys.Add(
                    $"  CONSTRAINT {PostgreSqlDialect.Quote(constraintName)} FOREIGN KEY ({PostgreSqlDialect.Quote(colName)}) " +
                    $"REFERENCES {ReferencedTable(fkAttr, schemaName)} ({PostgreSqlDialect.Quote(fkAttr.ReferenceColumn)}) " +
                    $"ON DELETE {onDelete} ON UPDATE {onUpdate}");
            }
        }

        // Composite indexes on class
        var compositeIndexes = entityType.GetCustomAttributes<CompositeIndexAttribute>();
        foreach (var ci in compositeIndexes)
        {
            var cols = string.Join(", ", ci.ColumnNames.Select(c => $"{PostgreSqlDialect.Quote(c)}"));
            // A unique composite stays a table constraint so ON CONFLICT can arbitrate on it.
            if (ci.Unique)
                uniqueIndexes.Add($"  CONSTRAINT {PostgreSqlDialect.Quote(ci.IndexName)} UNIQUE ({cols})");
            else
                indexes.Add($"CREATE INDEX IF NOT EXISTS {PostgreSqlDialect.Quote(ci.IndexName)} " +
                            $"ON {qualifiedTable} ({cols});");
        }

        var allDefs = columns.ToList();
        if (primaryKeys.Count > 0)
            allDefs.Add($"  PRIMARY KEY ({string.Join(", ", primaryKeys)})");
        allDefs.AddRange(uniqueIndexes);
        allDefs.AddRange(foreignKeys);

        sql.Append(string.Join(",\n", allDefs));
        sql.AppendLine();
        sql.Append(')');

        // Table options. PostgreSQL has no inline ENGINE/CHARSET clause: the character
        // set is a database-wide property, and the access method is only spelled out when
        // it is not the default heap.
        if (tableAttr.AccessMethod == TableAccessMethod.Custom
            && !string.IsNullOrWhiteSpace(tableAttr.AccessMethodName))
            sql.Append($" USING {PostgreSqlDialect.Quote(tableAttr.AccessMethodName)}");

        sql.Append(';');

        // Indexes and comments are separate statements. Npgsql sends the whole script in one
        // round trip and wraps it in an implicit transaction, so the table and its indexes
        // are still created atomically.
        foreach (var index in indexes)
        {
            sql.AppendLine();
            sql.Append(index);
        }

        if (!string.IsNullOrEmpty(tableAttr.Comment))
        {
            sql.AppendLine();
            sql.Append($"COMMENT ON TABLE {qualifiedTable} IS '{EscapeString(tableAttr.Comment)}';");
        }

        foreach (var prop in properties)
        {
            var colAttr = prop.GetCustomAttribute<ColumnAttribute>();
            if (string.IsNullOrEmpty(colAttr?.Comment)) continue;
            var colName = !string.IsNullOrEmpty(colAttr.Name) ? colAttr.Name! : prop.Name;
            sql.AppendLine();
            sql.Append($"COMMENT ON COLUMN {qualifiedTable}.{PostgreSqlDialect.Quote(colName)} " +
                       $"IS '{EscapeString(colAttr.Comment)}';");
        }

        foreach (var prop in properties)
        {
            var colAttr = prop.GetCustomAttribute<ColumnAttribute>();
            if (colAttr?.OnUpdateCurrentTimestamp != true) continue;
            var colName = !string.IsNullOrEmpty(colAttr.Name) ? colAttr.Name! : prop.Name;
            foreach (var stmt in BuildTouchTrigger(schemaName, tableName, colName))
            {
                sql.AppendLine();
                sql.Append(stmt);
            }
        }

        return sql.ToString();
    }

    // ── Model CRC ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Computes a stable CRC32 (8-char lowercase hex) of the entity's desired schema,
    /// derived from <see cref="GenerateCreateTable"/>. Used by the <c>__schema_state</c>
    /// sentinel: when the stored CRC matches this value the table is skipped without any
    /// <c>pg_catalog</c> diffing. The CRC is order-independent and ignores cosmetic
    /// differences (the <c>IF NOT EXISTS</c> noise and whitespace).
    /// </summary>
    public string ComputeSchemaCrc(Type entityType, string? connectionId = null) =>
        ComputeCrc(NormalizeForCrc(GenerateCreateTable(entityType, connectionId)));

    /// <summary>Computes a CRC32 (8-char lowercase hex) of an arbitrary string.</summary>
    public static string ComputeCrc(string text) =>
        Crc32(Encoding.UTF8.GetBytes(text)).ToString("x8");

    /// <summary>
    /// Returns the canonical, normalized form of a CREATE TABLE statement used as CRC input:
    /// strips <c>IF NOT EXISTS</c>, trims and drops blank lines, and sorts the remaining lines
    /// so reflection ordering can never change the hash for an unchanged model.
    /// </summary>
    internal static string NormalizeForCrc(string createTableDdl)
    {
        var stripped = createTableDdl.Replace("IF NOT EXISTS ", "", StringComparison.Ordinal);
        var lines = stripped
            .Split('\n')
            .Select(l => l.Trim().TrimEnd(','))
            .Where(l => l.Length > 0)
            .OrderBy(l => l, StringComparer.Ordinal);
        return string.Join("\n", lines);
    }

    // Minimal, self-contained CRC32 (IEEE 802.3 polynomial 0xEDB88320). Avoids a NuGet
    // dependency; the table is built once and cached.
    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
            crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    // ── Schema diff ────────────────────────────────────────────────────────────

    /// <summary>
    /// Compares the entity type definition against the live database schema and returns
    /// a list of ALTER TABLE statements needed to bring the table in sync at the given
    /// <see cref="SchemaSyncLevel"/>.
    /// </summary>
    public async Task<List<string>> GenerateAlterStatementsAsync(
        Type entityType,
        NpgsqlConnection connection,
        SchemaSyncLevel level = SchemaSyncLevel.Safe,
        CancellationToken ct = default,
        string? connectionId = null)
    {
        var defaultStringSize = PostgreSqlRuntimeOptions.For(connectionId).DefaultStringSize;
        var tableName = GetTableName(entityType);
        var schemaName = GetSchemaName(entityType, connectionId);
        var qualifiedTable = PostgreSqlDialect.Qualify(schemaName, tableName);
        var alterStatements = new List<string>();

        if (level == SchemaSyncLevel.None)
            return alterStatements;

        // Load the current schema from pg_catalog.
        var existingColumns = await GetExistingColumnsAsync(connection, tableName, schemaName, ct).ConfigureAwait(false);
        var existingIndexes = await GetExistingIndexesAsync(connection, tableName, schemaName, ct).ConfigureAwait(false);
        var existingFks = await GetExistingForeignKeysAsync(connection, tableName, schemaName, ct).ConfigureAwait(false);

        var properties = GetMappedProperties(entityType);

        // Build the set of column names + index names + FK names that the model expects.
        // Used later to know what to drop at Additive/Full.
        var modelColumnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var modelIndexNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PRIMARY" };
        var modelFkNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // previous → new column name, so an index on a column renamed in this pass is not drift.
        var renamedColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Creates a model index that is missing. One that exists under the same name with a
        // different definition is reported at every level, and rebuilt only at Additive+ (a
        // rebuild is a drop); at Safe the sync's Full-level check flags the table DriftPending.
        void ReconcileIndex(string idxName, IReadOnlyList<string> keyColumns, bool unique,
            IReadOnlyList<string> includeColumns, string createStatement)
        {
            if (!modelIndexNames.Add(idxName)) return;
            if (!existingIndexes.TryGetValue(idxName, out var existing))
            {
                alterStatements.Add(createStatement);
                return;
            }

            var existingKeys = existing.Columns.Select(c => renamedColumns.GetValueOrDefault(c, c)).ToArray();
            var existingIncludes = existing.IncludeColumns.Select(c => renamedColumns.GetValueOrDefault(c, c)).ToArray();
            if (existingKeys.SequenceEqual(keyColumns, StringComparer.OrdinalIgnoreCase)
                && existing.IsUnique == unique
                && existingIncludes.SequenceEqual(includeColumns, StringComparer.OrdinalIgnoreCase))
                return;

            _logger?.Warning(
                $"[PostgreSQL] Index `{idxName}` on table `{tableName}` has drifted. " +
                $"Existing: {DescribeIndex(existingKeys, existing.IsUnique, existingIncludes)}, " +
                $"Model: {DescribeIndex(keyColumns, unique, includeColumns)}.");
            if (level < SchemaSyncLevel.Additive) return;

            // An index that backs a UNIQUE constraint must be dropped via the constraint.
            alterStatements.Add(existing.IsConstraint
                ? $"ALTER TABLE {qualifiedTable} DROP CONSTRAINT {PostgreSqlDialect.Quote(idxName)};"
                : $"DROP INDEX IF EXISTS {PostgreSqlDialect.Qualify(schemaName, idxName)};");
            alterStatements.Add(createStatement);
        }

        foreach (var prop in properties)
        {
            var colAttr = prop.GetCustomAttribute<ColumnAttribute>();
            var colName = !string.IsNullOrEmpty(colAttr?.Name) ? colAttr.Name! : prop.Name;
            modelColumnNames.Add(colName);

            if (!existingColumns.ContainsKey(colName))
            {
                var previous = colAttr?.PreviousName;
                if (!string.IsNullOrEmpty(previous)
                    && !string.Equals(previous, colName, StringComparison.OrdinalIgnoreCase)
                    && existingColumns.ContainsKey(previous))
                {
                    // Renamed property — RENAME COLUMN preserves the data instead of the
                    // drop-old + add-new that would otherwise lose it. Works at Safe+.
                    // PostgreSQL renames without restating the type; any type change is
                    // picked up by the ColumnNeedsModify pass on the next sync.
                    alterStatements.Add(
                        $"ALTER TABLE {qualifiedTable} RENAME COLUMN {PostgreSqlDialect.Quote(previous)} " +
                        $"TO {PostgreSqlDialect.Quote(colName)};");
                    // The old name is consumed by the rename — keep it out of the Full drop set.
                    modelColumnNames.Add(previous);
                    renamedColumns[previous] = colName;
                    _logger?.Info($"[PostgreSQL] Will rename column `{tableName}`.`{previous}` → `{colName}`");
                }
                else
                {
                    // Column missing — ADD COLUMN
                    var colDef = BuildColumnDef(prop, colAttr, colName, defaultStringSize);
                    alterStatements.Add($"ALTER TABLE {qualifiedTable} ADD COLUMN {colDef};");
                    _logger?.Debug($"[PostgreSQL] Will add column `{tableName}`.`{colName}`");
                }
            }
            else
            {
                // Column exists — check if MODIFY is needed (Safe+)
                var existing = existingColumns[colName];
                if (ColumnNeedsModify(existing, prop, colAttr, defaultStringSize))
                {
                    // PostgreSQL has no single MODIFY COLUMN: type, nullability and default
                    // are each their own ALTER action.
                    alterStatements.AddRange(
                        BuildAlterColumnStatements(qualifiedTable, prop, colAttr, colName, existing, defaultStringSize));
                    _logger?.Debug($"[PostgreSQL] Will modify column \"{tableName}\".\"{colName}\"");
                }
            }

            // Single-column indexes
            if (colAttr?.Index == true && colAttr.Primary == false && colAttr.Unique == false)
            {
                var idxName = $"idx_{tableName}_{colName}";
                ReconcileIndex(idxName, [colName], unique: false, [],
                    $"CREATE INDEX IF NOT EXISTS {PostgreSqlDialect.Quote(idxName)} " +
                    $"ON {qualifiedTable} ({PostgreSqlDialect.Quote(colName)});");
            }

            if (colAttr?.Unique == true && colAttr.Primary == false)
            {
                var idxName = $"uq_{tableName}_{colName}";
                ReconcileIndex(idxName, [colName], unique: true, [],
                    $"ALTER TABLE {qualifiedTable} ADD CONSTRAINT {PostgreSqlDialect.Quote(idxName)} " +
                    $"UNIQUE ({PostgreSqlDialect.Quote(colName)});");
            }

            // [Index] attributes — same as CREATE TABLE path.
            foreach (var idxAttr in prop.GetCustomAttributes<IndexAttribute>())
            {
                var idxName = !string.IsNullOrEmpty(idxAttr.Name)
                    ? idxAttr.Name!
                    : (idxAttr.Unique ? $"uq_{tableName}_{colName}" : $"idx_{tableName}_{colName}");
                var includeCols = (idxAttr.Include ?? Array.Empty<string>())
                    .Select(propName => ResolveColumnName(entityType, propName))
                    .ToArray();
                var includeClause = includeCols.Length == 0
                    ? string.Empty
                    : $" INCLUDE ({string.Join(", ", includeCols.Select(PostgreSqlDialect.Quote))})";
                var keyword = idxAttr.Unique ? "CREATE UNIQUE INDEX" : "CREATE INDEX";
                ReconcileIndex(idxName, [colName], idxAttr.Unique, includeCols,
                    $"{keyword} IF NOT EXISTS {PostgreSqlDialect.Quote(idxName)} " +
                    $"ON {qualifiedTable} ({PostgreSqlDialect.Quote(colName)}){includeClause};");
            }

            // ON UPDATE CURRENT_TIMESTAMP equivalent. CREATE OR REPLACE / DROP IF EXISTS
            // make this idempotent, so it is emitted without probing the catalog first.
            if (colAttr?.OnUpdateCurrentTimestamp == true)
                alterStatements.AddRange(BuildTouchTrigger(schemaName, tableName, colName));

            // Foreign keys
            var fkAttr = prop.GetCustomAttribute<ForeignKeyAttribute>();
            if (fkAttr is not null)
            {
                var constraintName = fkAttr.ConstraintName
                    ?? $"fk_{tableName}_{colName}_{fkAttr.ReferenceTable}";
                modelFkNames.Add(constraintName);
                if (!existingFks.ContainsKey(constraintName))
                {
                    var onDelete = FkActionToSql(fkAttr.OnDelete);
                    var onUpdate = FkActionToSql(fkAttr.OnUpdate);
                    alterStatements.Add(
                        $"ALTER TABLE {qualifiedTable} ADD CONSTRAINT {PostgreSqlDialect.Quote(constraintName)} " +
                        $"FOREIGN KEY ({PostgreSqlDialect.Quote(colName)}) REFERENCES {ReferencedTable(fkAttr, schemaName)} ({PostgreSqlDialect.Quote(fkAttr.ReferenceColumn)}) " +
                        $"ON DELETE {onDelete} ON UPDATE {onUpdate};");
                }
            }
        }

        // Composite indexes
        var compositeIndexes = entityType.GetCustomAttributes<CompositeIndexAttribute>();
        foreach (var ci in compositeIndexes)
        {
            var cols = string.Join(", ", ci.ColumnNames.Select(c => $"{PostgreSqlDialect.Quote(c)}"));
            ReconcileIndex(ci.IndexName, ci.ColumnNames, ci.Unique, [], ci.Unique
                ? $"ALTER TABLE {qualifiedTable} ADD CONSTRAINT {PostgreSqlDialect.Quote(ci.IndexName)} UNIQUE ({cols});"
                : $"CREATE INDEX IF NOT EXISTS {PostgreSqlDialect.Quote(ci.IndexName)} ON {qualifiedTable} ({cols});");
        }

        // ── Level: Additive ── drop removed indexes and FKs
        if (level >= SchemaSyncLevel.Additive)
        {
            // Drop FKs that no longer exist in the model. Must drop FKs before indexes,
            // because some FK indexes may back the FK constraints.
            foreach (var fkName in existingFks.Keys)
            {
                if (modelFkNames.Contains(fkName)) continue;
                alterStatements.Add($"ALTER TABLE {qualifiedTable} DROP CONSTRAINT {PostgreSqlDialect.Quote(fkName)};");
                _logger?.Debug($"[PostgreSQL] Will drop foreign key `{tableName}`.`{fkName}`");
            }

            // Drop indexes no longer in the model. Skip PRIMARY (we don't manage PK drops here)
            // and skip auto-created FK backing indexes (they'll be cleaned up when the FK is dropped).
            foreach (var idxName in existingIndexes.Keys)
            {
                if (modelIndexNames.Contains(idxName)) continue;
                // The primary key's backing index is named <table>_pkey and is owned by the
                // constraint; dropping it directly is an error.
                if (idxName.EndsWith("_pkey", StringComparison.OrdinalIgnoreCase)) continue;
                if (existingFks.ContainsKey(idxName)) continue;
                // An index that backs a UNIQUE constraint must be dropped via the constraint.
                alterStatements.Add(existingIndexes[idxName].IsConstraint
                    ? $"ALTER TABLE {qualifiedTable} DROP CONSTRAINT {PostgreSqlDialect.Quote(idxName)};"
                    : $"DROP INDEX IF EXISTS {PostgreSqlDialect.Qualify(schemaName, idxName)};");
                _logger?.Debug($"[PostgreSQL] Will drop index `{tableName}`.`{idxName}`");
            }
        }

        // ── Level: Full ── drop removed columns
        if (level >= SchemaSyncLevel.Full)
        {
            foreach (var dbColName in existingColumns.Keys)
            {
                if (modelColumnNames.Contains(dbColName)) continue;
                alterStatements.Add($"ALTER TABLE {qualifiedTable} DROP COLUMN {PostgreSqlDialect.Quote(dbColName)};");
                _logger?.Warning($"[PostgreSQL] Will drop column `{tableName}`.`{dbColName}` (SchemaSyncLevel.Full)");
            }
        }

        return alterStatements;
    }

    // ── Column DDL builder ────────────────────────────────────────────────────

    private static string BuildColumnDef(
        PropertyInfo prop, ColumnAttribute? colAttr, string colName, int defaultStringSize = 255)
    {
        var sb = new StringBuilder();
        sb.Append($"{PostgreSqlDialect.Quote(colName)} ");

        // An absent attribute, or one that leaves DataType at Unspecified, is resolved
        // against the CLR property type inside GetPostgreSqlType — which also recovers the
        // inferred size (Guid -> CHAR(36), string -> VARCHAR(255)).
        var effective = colAttr ?? new ColumnAttribute();
        sb.Append(TypeConverter.GetPostgreSqlType(effective, effective.StorageType, prop.PropertyType, defaultStringSize));

        // Collation is per-column in PostgreSQL and only valid on collatable types.
        if (!string.IsNullOrEmpty(colAttr?.Charset))
            sb.Append($" COLLATE {PostgreSqlDialect.Quote(colAttr.Charset)}");

        // An identity column is implicitly NOT NULL and cannot also carry a DEFAULT.
        if (colAttr?.AutoIncrement == true)
        {
            sb.Append(" GENERATED BY DEFAULT AS IDENTITY");
        }
        else
        {
            if (ExpectsNotNull(prop, colAttr))
                sb.Append(" NOT NULL");
            // PostgreSQL columns are nullable by default, so NULL is never spelled out.

            if (!string.IsNullOrEmpty(colAttr?.DefaultValue))
                sb.Append($" DEFAULT {colAttr.DefaultValue}");
        }

        // ON UPDATE CURRENT_TIMESTAMP has no PostgreSQL equivalent -- it needs a trigger,
        // which TableSyncService emits separately. Comments are separate COMMENT ON statements.

        return sb.ToString();
    }

    // ── pg_catalog queries ────────────────────────────────────────────────────

    private static async Task<Dictionary<string, ColumnInfo>> GetExistingColumnsAsync(
        NpgsqlConnection connection,
        string tableName,
        string schemaName,
        CancellationToken ct)
    {
        var result = new Dictionary<string, ColumnInfo>(StringComparer.OrdinalIgnoreCase);

        await using var cmd = connection.CreateCommand();
        // format_type gives the canonical type text ("character varying(255)") that
        // BuildExpectedTypeString is written to match, so the comparison is like-for-like
        // instead of comparing a DDL spelling against a catalog type name.
        cmd.CommandText = """
            SELECT a.attname,
                   pg_catalog.format_type(a.atttypid, a.atttypmod),
                   NOT a.attnotnull                                AS is_nullable,
                   pg_catalog.pg_get_expr(d.adbin, d.adrelid)      AS col_default,
                   a.attidentity::text AS identity_kind,
                   coll.collname,
                   pg_catalog.col_description(c.oid, a.attnum)     AS col_comment
            FROM pg_catalog.pg_attribute a
            JOIN pg_catalog.pg_class c      ON c.oid = a.attrelid
            JOIN pg_catalog.pg_namespace n  ON n.oid = c.relnamespace
            LEFT JOIN pg_catalog.pg_attrdef d
                   ON d.adrelid = a.attrelid AND d.adnum = a.attnum
            LEFT JOIN pg_catalog.pg_collation coll ON coll.oid = a.attcollation
            WHERE n.nspname = @schema AND c.relname = @tbl
              AND a.attnum > 0 AND NOT a.attisdropped
            ORDER BY a.attnum
            """;
        cmd.Parameters.AddWithValue("@schema", schemaName);
        cmd.Parameters.AddWithValue("@tbl", tableName);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var name = reader.GetString(0);
            var identity = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
            result[name] = new ColumnInfo(
                Name: name,
                ColumnType: reader.GetString(1),
                IsNullable: reader.GetBoolean(2),
                DefaultValue: reader.IsDBNull(3) ? null : reader.GetString(3),
                // "Extra" carries identity-ness so the shared diff logic can treat it the way
                // the MySQL analyzer treats auto_increment.
                Extra: identity.Length > 0 ? "identity" : string.Empty,
                CharacterSet: reader.IsDBNull(5) ? null : reader.GetString(5),
                Comment: reader.IsDBNull(6) ? null : reader.GetString(6));
        }

        return result;
    }

    private static async Task<Dictionary<string, string>> GetExistingForeignKeysAsync(
        NpgsqlConnection connection,
        string tableName,
        string schemaName,
        CancellationToken ct)
    {
        // Maps constraint_name -> referenced table. Only existence matters today, but the
        // target is kept so a future migration can inspect it without re-querying.
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT con.conname, rel.relname
            FROM pg_catalog.pg_constraint con
            JOIN pg_catalog.pg_class c     ON c.oid = con.conrelid
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_catalog.pg_class rel   ON rel.oid = con.confrelid
            WHERE con.contype = 'f' AND n.nspname = @schema AND c.relname = @tbl
            """;
        cmd.Parameters.AddWithValue("@schema", schemaName);
        cmd.Parameters.AddWithValue("@tbl", tableName);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            result[reader.GetString(0)] = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);

        return result;
    }

    private static async Task<Dictionary<string, IndexInfo>> GetExistingIndexesAsync(
        NpgsqlConnection connection,
        string tableName,
        string schemaName,
        CancellationToken ct)
    {
        var result = new Dictionary<string, IndexInfo>(StringComparer.OrdinalIgnoreCase);

        await using var cmd = connection.CreateCommand();
        // conindid links an index to the constraint that owns it. An owned index has to be
        // dropped through ALTER TABLE DROP CONSTRAINT, never DROP INDEX.
        cmd.CommandText = """
            SELECT ic.relname,
                   i.indisunique,
                   EXISTS (SELECT 1 FROM pg_catalog.pg_constraint con
                           WHERE con.conindid = i.indexrelid) AS is_constraint,
                   ARRAY(SELECT a.attname::text
                         FROM unnest(i.indkey) WITH ORDINALITY AS k(attnum, ord)
                         JOIN pg_catalog.pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum
                         WHERE k.ord <= i.indnkeyatts
                         ORDER BY k.ord) AS key_columns,
                   ARRAY(SELECT a.attname::text
                         FROM unnest(i.indkey) WITH ORDINALITY AS k(attnum, ord)
                         JOIN pg_catalog.pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum
                         WHERE k.ord > i.indnkeyatts
                         ORDER BY k.ord) AS include_columns
            FROM pg_catalog.pg_index i
            JOIN pg_catalog.pg_class ic    ON ic.oid = i.indexrelid
            JOIN pg_catalog.pg_class c     ON c.oid = i.indrelid
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND c.relname = @tbl
            """;
        cmd.Parameters.AddWithValue("@schema", schemaName);
        cmd.Parameters.AddWithValue("@tbl", tableName);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var name = reader.GetString(0);
            result[name] = new IndexInfo(name, reader.GetBoolean(1), reader.GetBoolean(2),
                reader.GetFieldValue<string[]>(3), reader.GetFieldValue<string[]>(4));
        }

        return result;
    }

    /// <summary>
    /// Builds the statements behind <see cref="Models.ColumnAttribute.OnUpdateCurrentTimestamp"/>.
    /// PostgreSQL has no such column clause, so a <c>BEFORE UPDATE</c> row trigger assigns
    /// the column instead. The trigger function is per column so that two touch columns on
    /// one table do not collide, and every statement is idempotent.
    /// </summary>
    private static IEnumerable<string> BuildTouchTrigger(string schemaName, string tableName, string colName)
    {
        var qualifiedTable = PostgreSqlDialect.Qualify(schemaName, tableName);
        var fnName = $"fn_{tableName}_{colName}_touch";
        var trgName = $"trg_{tableName}_{colName}_touch";
        var qualifiedFn = PostgreSqlDialect.Qualify(schemaName, fnName);

        yield return
            $"CREATE OR REPLACE FUNCTION {qualifiedFn}() RETURNS trigger AS $$\n" +
            $"BEGIN NEW.{PostgreSqlDialect.Quote(colName)} = now(); RETURN NEW; END;\n" +
            $"$$ LANGUAGE plpgsql;";

        // CREATE OR REPLACE TRIGGER is PostgreSQL 14+; drop-then-create also works on 12/13.
        yield return $"DROP TRIGGER IF EXISTS {PostgreSqlDialect.Quote(trgName)} ON {qualifiedTable};";
        yield return
            $"CREATE TRIGGER {PostgreSqlDialect.Quote(trgName)} BEFORE UPDATE ON {qualifiedTable} " +
            $"FOR EACH ROW EXECUTE FUNCTION {qualifiedFn}();";
    }

    /// <summary>
    /// Renders the referenced side of a foreign key, schema-qualified. A reference table may
    /// itself be written as <c>schema.table</c>; otherwise it is assumed to live in the same
    /// schema as the referencing table.
    /// </summary>
    private static string ReferencedTable(ForeignKeyAttribute fk, string owningSchema) =>
        fk.ReferenceTable.Contains('.', StringComparison.Ordinal)
            ? PostgreSqlDialect.QuoteMultipart(fk.ReferenceTable)
            : PostgreSqlDialect.Qualify(owningSchema, fk.ReferenceTable);

    /// <summary>
    /// Expands a column change into the separate ALTER actions PostgreSQL requires. MySQL
    /// restates the whole column with MODIFY COLUMN; PostgreSQL alters type, nullability and
    /// default independently, and only the differing facets are emitted.
    /// </summary>
    private static List<string> BuildAlterColumnStatements(
        string qualifiedTable,
        PropertyInfo prop,
        ColumnAttribute? colAttr,
        string colName,
        ColumnInfo existing,
        int defaultStringSize = 255)
    {
        var statements = new List<string>();
        var quoted = PostgreSqlDialect.Quote(colName);
        var expectedType = BuildExpectedTypeString(prop, colAttr, defaultStringSize);

        if (!TypesEquivalent(existing.ColumnType, expectedType))
        {
            // USING makes otherwise-illegal widenings (text -> uuid, int -> bigint) explicit.
            statements.Add(
                $"ALTER TABLE {qualifiedTable} ALTER COLUMN {quoted} TYPE {expectedType} " +
                $"USING {quoted}::{expectedType};");
        }

        var expectNotNull = ExpectsNotNull(prop, colAttr);
        if (expectNotNull == existing.IsNullable)
        {
            statements.Add(expectNotNull
                ? $"ALTER TABLE {qualifiedTable} ALTER COLUMN {quoted} SET NOT NULL;"
                : $"ALTER TABLE {qualifiedTable} ALTER COLUMN {quoted} DROP NOT NULL;");
        }

        var expectedDefault = colAttr?.DefaultValue;
        var hasDefault = !string.IsNullOrEmpty(expectedDefault);
        // An identity column owns its default; never fight it.
        if (colAttr?.AutoIncrement != true)
        {
            if (hasDefault && !DefaultsEquivalent(existing.DefaultValue, expectedDefault))
                statements.Add($"ALTER TABLE {qualifiedTable} ALTER COLUMN {quoted} SET DEFAULT {expectedDefault};");
            else if (!hasDefault && existing.DefaultValue is not null)
                statements.Add($"ALTER TABLE {qualifiedTable} ALTER COLUMN {quoted} DROP DEFAULT;");
        }

        return statements;
    }

    /// <summary>
    /// Compares a type as PostgreSQL reports it against the type the model asks for.
    /// <para>
    /// Both sides are produced in the same canonical vocabulary — <c>format_type</c> on the
    /// database side, <see cref="TypeConverter.GetPostgreSqlType"/> on the model side — so
    /// the comparison is mostly literal. Aliases still need folding, because a column
    /// declared <c>varchar(255)</c> is reported as <c>character varying(255)</c>, and an
    /// unqualified <c>numeric</c> matches any precision.
    /// </para>
    /// </summary>
    internal static bool TypesEquivalent(string dbType, string modelType)
    {
        var a = CanonicalType(dbType);
        var b = CanonicalType(modelType);
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;

        // "numeric" with no precision accepts any precision; likewise bare varchar/char.
        var aBase = BaseOf(a);
        var bBase = BaseOf(b);
        if (!string.Equals(aBase, bBase, StringComparison.OrdinalIgnoreCase)) return false;
        return !HasArgs(a) || !HasArgs(b);

        static bool HasArgs(string t) => t.Contains('(', StringComparison.Ordinal);
        static string BaseOf(string t)
        {
            var idx = t.IndexOf('(', StringComparison.Ordinal);
            return (idx < 0 ? t : t[..idx]).Trim();
        }
    }

    /// <summary>Folds PostgreSQL's type aliases onto their canonical spelling.</summary>
    private static string CanonicalType(string type)
    {
        var t = type.Trim().ToLowerInvariant();
        // Array suffix is preserved through the alias fold.
        var isArray = t.EndsWith("[]", StringComparison.Ordinal);
        if (isArray) t = t[..^2].TrimEnd();

        var args = string.Empty;
        var open = t.IndexOf('(', StringComparison.Ordinal);
        if (open >= 0)
        {
            args = t[open..];
            t = t[..open].Trim();
        }

        t = t switch
        {
            "varchar"                     => "character varying",
            "char" or "bpchar"            => "character",
            "int" or "int4" or "integer"  => "integer",
            "int2" or "smallint"          => "smallint",
            "int8" or "bigint"            => "bigint",
            "float4" or "real"            => "real",
            "float8"                      => "double precision",
            "decimal"                     => "numeric",
            "bool"                        => "boolean",
            "timestamptz"                 => "timestamp with time zone",
            "timestamp"                   => "timestamp without time zone",
            "timetz"                      => "time with time zone",
            "time"                        => "time without time zone",
            "varbit"                      => "bit varying",
            _                             => t
        };

        // character(1) and character are the same type; so are numeric and numeric(p,s)
        // only when the model leaves precision unspecified, which BaseOf handles.
        return isArray ? t + args + "[]" : t + args;
    }

    /// <summary>
    /// Compares a stored default against the model's. PostgreSQL echoes defaults back with a
    /// type cast appended (<c>'active'::text</c>, <c>now()</c>), so the cast is stripped
    /// before comparing.
    /// </summary>
    private static bool DefaultsEquivalent(string? dbDefault, string? modelDefault)
    {
        if (dbDefault is null || modelDefault is null) return dbDefault == modelDefault;
        return string.Equals(StripCast(dbDefault), StripCast(modelDefault), StringComparison.OrdinalIgnoreCase);

        static string StripCast(string value)
        {
            var v = value.Trim();
            var castIdx = v.IndexOf("::", StringComparison.Ordinal);
            if (castIdx > 0) v = v[..castIdx].Trim();
            // CURRENT_TIMESTAMP and now() are the same function.
            if (v.Equals("now()", StringComparison.OrdinalIgnoreCase)) v = "CURRENT_TIMESTAMP";
            return v.Trim();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Decides whether a live column differs from the model in a way that needs DDL —
    /// type/size, nullability, identity-ness, default, collation or comment.
    /// <para>
    /// Every facet is compared in a canonical form rather than by raw string equality.
    /// That matters here because PostgreSQL reports types through
    /// <c>format_type</c> ("character varying(255)", "timestamp with time zone") while the
    /// model generates its own spelling, and defaults come back with a type cast attached.
    /// Comparing those literally would report a difference on nearly every column of every
    /// table and rewrite the whole database on each boot.
    /// </para>
    /// </summary>
    private bool ColumnNeedsModify(
        ColumnInfo existing, PropertyInfo prop, ColumnAttribute? colAttr, int defaultStringSize = 255)
    {
        // 1. Data type, folded through the alias table and ignoring unspecified precision.
        var expectedType = BuildExpectedTypeString(prop, colAttr, defaultStringSize);
        if (!TypesEquivalent(existing.ColumnType, expectedType))
        {
            _logger?.Debug(
                $"[PostgreSQL] column \"{existing.Name}\" type mismatch: model={expectedType}, db={existing.ColumnType}");
            return true;
        }

        // 2. Nullability.
        var expectNullable = !ExpectsNotNull(prop, colAttr);
        if (existing.IsNullable != expectNullable)
        {
            _logger?.Debug(
                $"[PostgreSQL] column \"{existing.Name}\" nullability mismatch: model={expectNullable}, db={existing.IsNullable}");
            return true;
        }

        // 3. Identity. GetExistingColumnsAsync reports attidentity as "identity" in Extra.
        var expectIdentity = colAttr?.AutoIncrement == true;
        var dbIdentity = existing.Extra.Contains("identity", StringComparison.OrdinalIgnoreCase);
        if (expectIdentity != dbIdentity)
        {
            _logger?.Debug(
                $"[PostgreSQL] column \"{existing.Name}\" identity mismatch: model={expectIdentity}, db={dbIdentity}");
            return true;
        }

        // 4. Default value. An identity column's default is owned by its sequence, so it is
        //    not compared — doing so would report a permanent difference.
        if (!expectIdentity)
        {
            var modelDefault = colAttr?.DefaultValue;
            var hasModelDefault = !string.IsNullOrEmpty(modelDefault);
            if (hasModelDefault != (existing.DefaultValue is not null)
                || (hasModelDefault && !DefaultsEquivalent(existing.DefaultValue, modelDefault)))
            {
                _logger?.Debug(
                    $"[PostgreSQL] column \"{existing.Name}\" default mismatch: " +
                    $"model='{modelDefault}', db='{existing.DefaultValue}'");
                return true;
            }
        }

        // 5. Collation, for collatable types only, and only when the model states one.
        //    PostgreSQL reports the database-wide default as "default".
        if (!string.IsNullOrWhiteSpace(colAttr?.Charset) &&
            IsStringType(TypeConverter.ResolveColumn(colAttr!, prop.PropertyType).DataType))
        {
            var dbCollation = existing.CharacterSet ?? "";
            if (!string.Equals(colAttr!.Charset, dbCollation, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.Debug(
                    $"[PostgreSQL] column \"{existing.Name}\" collation mismatch: model={colAttr.Charset}, db={dbCollation}");
                return true;
            }
        }

        // Comments are deliberately not compared here: COMMENT ON is a separate statement,
        // so a changed comment must not trigger a column rewrite.
        return false;
    }

    private static string BuildExpectedTypeString(
        PropertyInfo prop, ColumnAttribute? colAttr, int defaultStringSize = 255)
    {
        var effective = colAttr ?? new ColumnAttribute();
        return TypeConverter.GetPostgreSqlType(
            effective, effective.StorageType, prop.PropertyType, defaultStringSize);
    }

    private static string NormalizeDefault(string? value, bool isNullable)
    {
        if (value is null)
            return isNullable ? "NULL" : "";
        var trimmed = value.Trim();
        // Strip surrounding quotes that PostgreSQL sometimes returns for string defaults
        if (trimmed.Length >= 2 && trimmed.StartsWith('\'') && trimmed.EndsWith('\''))
            trimmed = trimmed[1..^1];
        return trimmed.ToUpperInvariant();
    }

    private static bool IsStringType(DataType? type) =>
        type is DataType.VarChar or DataType.Char or DataType.Text;

    /// <summary>
    /// The single rule for whether a column is NOT NULL, used by CREATE TABLE, by the diff
    /// detector and by the ALTER emitter alike. Keeping one rule matters: if the create path
    /// and the diff path disagree, every sync "fixes" a column that was just created that
    /// way and the table is altered on every boot forever.
    /// <para>
    /// A non-nullable CLR value type maps to NOT NULL. Reference types stay nullable —
    /// nullable-reference annotations are not visible through reflection here, so a
    /// <c>string</c> property cannot be distinguished from a <c>string?</c> one.
    /// </para>
    /// </summary>
    private static bool ExpectsNotNull(PropertyInfo prop, ColumnAttribute? colAttr) =>
        colAttr?.NotNull == true
        || colAttr?.Primary == true
        || colAttr?.AutoIncrement == true
        || !IsNullableType(prop.PropertyType);

    private static bool IsNullableType(Type type) =>
        !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;

    private static PropertyInfo[] GetMappedProperties(Type entityType) =>
        entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetCustomAttribute<IgnoreAttribute>() is null)
            .ToArray();

    private static string FkActionToSql(ForeignKeyAction action) => action switch
    {
        ForeignKeyAction.Cascade    => "CASCADE",
        ForeignKeyAction.SetNull    => "SET NULL",
        ForeignKeyAction.NoAction   => "NO ACTION",
        ForeignKeyAction.SetDefault => "SET DEFAULT",
        _                           => "RESTRICT"
    };


    private static string EscapeString(string s) => s.Replace("'", "''");

    /// <summary>
    /// Map a property name (as written in <c>[Index(Include = …)]</c>) to the underlying
    /// column name on the entity type. Missing properties throw a helpful error so
    /// typos fail at schema-sync time, not silently at query time.
    /// </summary>
    private static string ResolveColumnName(Type entityType, string propertyName)
    {
        var prop = entityType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new ArgumentException(
                $"[Index(Include)] refers to '{propertyName}' but type '{entityType.Name}' has no such property.");
        var attr = prop.GetCustomAttribute<ColumnAttribute>();
        return !string.IsNullOrEmpty(attr?.Name) ? attr.Name! : prop.Name;
    }

    // ── Internal record types ─────────────────────────────────────────────────

    private record ColumnInfo(
        string Name,
        string ColumnType,
        bool IsNullable,
        string? DefaultValue,
        string Extra,
        string? CharacterSet,
        string? Comment);

    private record IndexInfo(string Name, bool IsUnique, bool IsConstraint,
        IReadOnlyList<string> Columns, IReadOnlyList<string> IncludeColumns);

    private static string DescribeIndex(IReadOnlyList<string> keyColumns, bool unique, IReadOnlyList<string> includeColumns) =>
        $"{(unique ? "UNIQUE " : "")}({string.Join(", ", keyColumns)})" +
        (includeColumns.Count == 0 ? "" : $" INCLUDE ({string.Join(", ", includeColumns)})");
}
