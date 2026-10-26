using System.Reflection;
using System.Text;
using CL.MySQL2.Models;
using CodeLogic.Core.Logging;
using MySqlConnector;

namespace CL.MySQL2.Core;

/// <summary>
/// Analyzes entity types and generates the DDL statements required to create or alter
/// the corresponding MySQL table so it matches the current class definition.
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

    // ── CREATE TABLE ──────────────────────────────────────────────────────────

    /// <summary>
    /// Generates a CREATE TABLE IF NOT EXISTS statement for the given entity type.
    /// </summary>
    public string GenerateCreateTable(Type entityType)
    {
        var tableAttr = entityType.GetCustomAttribute<TableAttribute>() ?? new TableAttribute();
        var tableName = !string.IsNullOrEmpty(tableAttr.Name) ? tableAttr.Name! : entityType.Name;

        var sql = new StringBuilder();
        sql.AppendLine($"CREATE TABLE IF NOT EXISTS {MySqlDialect.Quote(tableName)} (");

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
            var colDef = BuildColumnDef(prop, colAttr, colName);
            columns.Add($"  {colDef}");

            if (colAttr?.Primary == true)
                primaryKeys.Add($"{MySqlDialect.Quote(colName)}");

            if (colAttr?.Index == true && colAttr.Primary == false && colAttr.Unique == false)
                indexes.Add($"  INDEX `idx_{tableName}_{colName}` ({MySqlDialect.Quote(colName)})");

            if (colAttr?.Unique == true && colAttr.Primary == false)
                uniqueIndexes.Add($"  UNIQUE KEY `uq_{tableName}_{colName}` ({MySqlDialect.Quote(colName)})");

            // [Index] attributes — modern form supporting covering Include columns.
            foreach (var idxAttr in prop.GetCustomAttributes<IndexAttribute>())
            {
                var idxName = !string.IsNullOrEmpty(idxAttr.Name)
                    ? idxAttr.Name!
                    : (idxAttr.Unique ? $"uq_{tableName}_{colName}" : $"idx_{tableName}_{colName}");
                var includeCols = (idxAttr.Include ?? Array.Empty<string>())
                    .Select(propName => ResolveColumnName(entityType, propName))
                    .ToArray();
                var colList = string.Join(", ",
                    new[] { $"{MySqlDialect.Quote(colName)}" }.Concat(includeCols.Select(c => $"{MySqlDialect.Quote(c)}")));
                if (idxAttr.Unique) uniqueIndexes.Add($"  UNIQUE KEY {MySqlDialect.Quote(idxName)} ({colList})");
                else                indexes.Add($"  INDEX {MySqlDialect.Quote(idxName)} ({colList})");
            }

            // Foreign key
            var fkAttr = prop.GetCustomAttribute<ForeignKeyAttribute>();
            if (fkAttr is not null)
            {
                var constraintName = fkAttr.ConstraintName
                    ?? $"fk_{tableName}_{colName}_{fkAttr.ReferenceTable}";
                var onDelete = FkActionToSql(fkAttr.OnDelete);
                var onUpdate = FkActionToSql(fkAttr.OnUpdate);
                foreignKeys.Add(
                    $"  CONSTRAINT {MySqlDialect.Quote(constraintName)} FOREIGN KEY ({MySqlDialect.Quote(colName)}) " +
                    $"REFERENCES {MySqlDialect.Quote(fkAttr.ReferenceTable)} ({MySqlDialect.Quote(fkAttr.ReferenceColumn)}) " +
                    $"ON DELETE {onDelete} ON UPDATE {onUpdate}");
            }
        }

        // Composite indexes on class
        var compositeIndexes = entityType.GetCustomAttributes<CompositeIndexAttribute>();
        foreach (var ci in compositeIndexes)
        {
            var cols = string.Join(", ", ci.ColumnNames.Select(c => $"{MySqlDialect.Quote(c)}"));
            if (ci.Unique)
                uniqueIndexes.Add($"  UNIQUE KEY {MySqlDialect.Quote(ci.IndexName)} ({cols})");
            else
                indexes.Add($"  INDEX {MySqlDialect.Quote(ci.IndexName)} ({cols})");
        }

        var allDefs = columns.ToList();
        if (primaryKeys.Count > 0)
            allDefs.Add($"  PRIMARY KEY ({string.Join(", ", primaryKeys)})");
        allDefs.AddRange(indexes);
        allDefs.AddRange(uniqueIndexes);
        allDefs.AddRange(foreignKeys);

        sql.Append(string.Join(",\n", allDefs));
        sql.AppendLine();
        sql.Append(')');

        // Table options
        var engineName = tableAttr.Engine.ToString().ToUpperInvariant();
        var charsetName = CharsetToString(tableAttr.Charset);
        sql.Append($" ENGINE={engineName} DEFAULT CHARSET={charsetName}");

        if (!string.IsNullOrEmpty(tableAttr.Collation))
            sql.Append($" COLLATE={tableAttr.Collation}");

        if (!string.IsNullOrEmpty(tableAttr.Comment))
            sql.Append($" COMMENT='{EscapeString(tableAttr.Comment)}'");

        sql.Append(';');
        return sql.ToString();
    }

    // ── Model CRC ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Computes a stable CRC32 (8-char lowercase hex) of the entity's desired schema,
    /// derived from <see cref="GenerateCreateTable"/>. Used by the <c>__schema_state</c>
    /// sentinel: when the stored CRC matches this value the table is skipped without any
    /// <c>information_schema</c> diffing. The CRC is order-independent and ignores cosmetic
    /// differences (the <c>IF NOT EXISTS</c> noise and whitespace).
    /// </summary>
    public string ComputeSchemaCrc(Type entityType) =>
        ComputeCrc(NormalizeForCrc(GenerateCreateTable(entityType)));

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
        MySqlConnection connection,
        SchemaSyncLevel level = SchemaSyncLevel.Safe,
        CancellationToken ct = default)
    {
        var tableName = GetTableName(entityType);
        var alterStatements = new List<string>();

        if (level == SchemaSyncLevel.None)
            return alterStatements;

        // Load current schema from INFORMATION_SCHEMA
        var existingColumns = await GetExistingColumnsAsync(connection, tableName, ct).ConfigureAwait(false);
        var existingIndexes = await GetExistingIndexesAsync(connection, tableName, ct).ConfigureAwait(false);
        var existingFks = await GetExistingForeignKeysAsync(connection, tableName, ct).ConfigureAwait(false);

        var properties = GetMappedProperties(entityType);

        // Build the set of column names + index names + FK names that the model expects.
        // Used later to know what to drop at Additive/Full.
        var modelColumnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var modelIndexNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PRIMARY" };
        var modelFkNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // previous → new column name, so an index on a column renamed in this pass is not drift.
        var renamedColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var quotedTable = MySqlDialect.Quote(tableName);

        // Creates a model index that is missing. One that exists under the same name with a
        // different definition is reported at every level, and rebuilt only at Additive+ (a
        // rebuild is a drop); at Safe the sync's Full-level check flags the table DriftPending.
        void ReconcileIndex(string idxName, IReadOnlyList<string> columns, bool unique)
        {
            if (!modelIndexNames.Add(idxName)) return;
            var add = $"ADD {(unique ? "UNIQUE KEY" : "INDEX")} {MySqlDialect.Quote(idxName)} " +
                      $"({string.Join(", ", columns.Select(MySqlDialect.Quote))})";
            if (!existingIndexes.TryGetValue(idxName, out var existing))
            {
                alterStatements.Add($"ALTER TABLE {quotedTable} {add};");
                return;
            }

            var existingColumns = existing.Columns.Select(c => renamedColumns.GetValueOrDefault(c, c)).ToArray();
            if (existingColumns.SequenceEqual(columns, StringComparer.OrdinalIgnoreCase) && existing.IsUnique == unique)
                return;

            _logger?.Warning(
                $"[MySQL2] Index `{idxName}` on table `{tableName}` has drifted. " +
                $"Existing: {(existing.IsUnique ? "UNIQUE " : "")}({string.Join(", ", existingColumns)}), " +
                $"Model: {(unique ? "UNIQUE " : "")}({string.Join(", ", columns)}).");
            if (level < SchemaSyncLevel.Additive) return;

            // One ALTER swaps the definition atomically, and MySQL accepts it even when the
            // index backs a foreign key.
            alterStatements.Add($"ALTER TABLE {quotedTable} DROP INDEX {MySqlDialect.Quote(idxName)}, {add};");
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
                    // Renamed property — CHANGE COLUMN preserves the data instead of the
                    // drop-old + add-new that would otherwise lose it. Works at Safe+.
                    var colDef = BuildColumnDef(prop, colAttr, colName);
                    alterStatements.Add($"ALTER TABLE {MySqlDialect.Quote(tableName)} CHANGE COLUMN {MySqlDialect.Quote(previous)} {colDef};");
                    // The old name is consumed by the rename — keep it out of the Full drop set.
                    modelColumnNames.Add(previous);
                    renamedColumns[previous] = colName;
                    _logger?.Info($"[MySQL2] Will rename column `{tableName}`.`{previous}` → `{colName}`");
                }
                else
                {
                    // Column missing — ADD COLUMN
                    var colDef = BuildColumnDef(prop, colAttr, colName);
                    alterStatements.Add($"ALTER TABLE {MySqlDialect.Quote(tableName)} ADD COLUMN {colDef};");
                    _logger?.Debug($"[MySQL2] Will add column `{tableName}`.`{colName}`");
                }
            }
            else
            {
                // Column exists — check if MODIFY is needed (Safe+)
                var existing = existingColumns[colName];
                if (ColumnNeedsModify(existing, prop, colAttr))
                {
                    var modifyDef = BuildColumnDef(prop, colAttr, colName);
                    alterStatements.Add($"ALTER TABLE {MySqlDialect.Quote(tableName)} MODIFY COLUMN {modifyDef};");
                    _logger?.Debug($"[MySQL2] Will modify column `{tableName}`.`{colName}`");
                }
            }

            // Single-column indexes
            if (colAttr?.Index == true && colAttr.Primary == false && colAttr.Unique == false)
                ReconcileIndex($"idx_{tableName}_{colName}", [colName], unique: false);

            if (colAttr?.Unique == true && colAttr.Primary == false)
                ReconcileIndex($"uq_{tableName}_{colName}", [colName], unique: true);

            // [Index] attributes — same as CREATE TABLE path. MySQL has no INCLUDE clause, so
            // the covering columns are appended to the key.
            foreach (var idxAttr in prop.GetCustomAttributes<IndexAttribute>())
            {
                var idxName = !string.IsNullOrEmpty(idxAttr.Name)
                    ? idxAttr.Name!
                    : (idxAttr.Unique ? $"uq_{tableName}_{colName}" : $"idx_{tableName}_{colName}");
                var includeCols = (idxAttr.Include ?? Array.Empty<string>())
                    .Select(propName => ResolveColumnName(entityType, propName));
                ReconcileIndex(idxName, [colName, .. includeCols], idxAttr.Unique);
            }

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
                        $"ALTER TABLE {MySqlDialect.Quote(tableName)} ADD CONSTRAINT {MySqlDialect.Quote(constraintName)} " +
                        $"FOREIGN KEY ({MySqlDialect.Quote(colName)}) REFERENCES {MySqlDialect.Quote(fkAttr.ReferenceTable)} ({MySqlDialect.Quote(fkAttr.ReferenceColumn)}) " +
                        $"ON DELETE {onDelete} ON UPDATE {onUpdate};");
                }
            }
        }

        // Composite indexes
        var compositeIndexes = entityType.GetCustomAttributes<CompositeIndexAttribute>();
        foreach (var ci in compositeIndexes)
            ReconcileIndex(ci.IndexName, ci.ColumnNames, ci.Unique);

        // ── Level: Additive ── drop removed indexes and FKs
        if (level >= SchemaSyncLevel.Additive)
        {
            // Drop FKs that no longer exist in the model. Must drop FKs before indexes,
            // because some FK indexes may back the FK constraints.
            foreach (var fkName in existingFks.Keys)
            {
                if (modelFkNames.Contains(fkName)) continue;
                alterStatements.Add($"ALTER TABLE {MySqlDialect.Quote(tableName)} DROP FOREIGN KEY {MySqlDialect.Quote(fkName)};");
                _logger?.Debug($"[MySQL2] Will drop foreign key `{tableName}`.`{fkName}`");
            }

            // Drop indexes no longer in the model. Skip PRIMARY (we don't manage PK drops here)
            // and skip auto-created FK backing indexes (they'll be cleaned up when the FK is dropped).
            foreach (var idxName in existingIndexes.Keys)
            {
                if (modelIndexNames.Contains(idxName)) continue;
                if (string.Equals(idxName, "PRIMARY", StringComparison.OrdinalIgnoreCase)) continue;
                // Skip indexes that share a name with an existing FK (auto-created by MySQL for FK)
                if (existingFks.ContainsKey(idxName)) continue;
                alterStatements.Add($"ALTER TABLE {MySqlDialect.Quote(tableName)} DROP INDEX {MySqlDialect.Quote(idxName)};");
                _logger?.Debug($"[MySQL2] Will drop index `{tableName}`.`{idxName}`");
            }
        }

        // ── Level: Full ── drop removed columns
        if (level >= SchemaSyncLevel.Full)
        {
            foreach (var dbColName in existingColumns.Keys)
            {
                if (modelColumnNames.Contains(dbColName)) continue;
                alterStatements.Add($"ALTER TABLE {MySqlDialect.Quote(tableName)} DROP COLUMN {MySqlDialect.Quote(dbColName)};");
                _logger?.Warning($"[MySQL2] Will drop column `{tableName}`.`{dbColName}` (SchemaSyncLevel.Full)");
            }
        }

        return alterStatements;
    }

    // ── Column DDL builder ────────────────────────────────────────────────────

    private static string BuildColumnDef(PropertyInfo prop, ColumnAttribute? colAttr, string colName)
    {
        var sb = new StringBuilder();
        sb.Append($"{MySqlDialect.Quote(colName)} ");

        // An absent attribute, or one that leaves DataType at Unspecified, is resolved
        // against the CLR property type inside GetMySqlType — which also recovers the
        // inferred size (Guid -> CHAR(36), string -> VARCHAR(255)).
        var effective = colAttr ?? new ColumnAttribute();
        sb.Append(TypeConverter.GetMySqlType(effective, effective.StorageType, prop.PropertyType));

        if (!string.IsNullOrEmpty(colAttr?.Charset))
            sb.Append($" CHARACTER SET {colAttr.Charset}");

        if (colAttr?.NotNull == true || colAttr?.Primary == true)
            sb.Append(" NOT NULL");
        else if (IsNullableType(prop.PropertyType))
            sb.Append(" NULL");

        if (colAttr?.AutoIncrement == true)
            sb.Append(" AUTO_INCREMENT");

        if (ModelDefaultSql(prop, colAttr) is { } defaultSql)
            sb.Append($" DEFAULT {defaultSql}");

        if (colAttr?.OnUpdateCurrentTimestamp == true)
            sb.Append(" ON UPDATE CURRENT_TIMESTAMP");

        if (!string.IsNullOrEmpty(colAttr?.Comment))
            sb.Append($" COMMENT '{EscapeString(colAttr.Comment)}'");

        return sb.ToString();
    }

    // ── INFORMATION_SCHEMA queries ────────────────────────────────────────────

    private static async Task<Dictionary<string, ColumnInfo>> GetExistingColumnsAsync(
        MySqlConnection connection,
        string tableName,
        CancellationToken ct)
    {
        var result = new Dictionary<string, ColumnInfo>(StringComparer.OrdinalIgnoreCase);
        var dbName = connection.Database;

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE, COLUMN_DEFAULT,
                   EXTRA, CHARACTER_SET_NAME, COLLATION_NAME, COLUMN_COMMENT
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @tbl
            ORDER BY ORDINAL_POSITION";
        cmd.Parameters.AddWithValue("@db", dbName);
        cmd.Parameters.AddWithValue("@tbl", tableName);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var name = reader.GetString(0);
            result[name] = new ColumnInfo(
                Name: name,
                ColumnType: reader.GetString(1),
                IsNullable: reader.GetString(2) == "YES",
                DefaultValue: reader.IsDBNull(3) ? null : reader.GetString(3),
                Extra: reader.GetString(4),
                CharacterSet: reader.IsDBNull(5) ? null : reader.GetString(5),
                Comment: reader.IsDBNull(7) ? null : reader.GetString(7));
        }

        return result;
    }

    private static async Task<Dictionary<string, string>> GetExistingForeignKeysAsync(
        MySqlConnection connection,
        string tableName,
        CancellationToken ct)
    {
        // Returns a map of constraint_name → referenced_table. We only need existence, but
        // store the target so future migrations can inspect it without re-querying.
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dbName = connection.Database;

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT CONSTRAINT_NAME, REFERENCED_TABLE_NAME
            FROM information_schema.KEY_COLUMN_USAGE
            WHERE TABLE_SCHEMA = @db
              AND TABLE_NAME = @tbl
              AND REFERENCED_TABLE_NAME IS NOT NULL
            GROUP BY CONSTRAINT_NAME, REFERENCED_TABLE_NAME";
        cmd.Parameters.AddWithValue("@db", dbName);
        cmd.Parameters.AddWithValue("@tbl", tableName);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result[reader.GetString(0)] = reader.IsDBNull(1) ? "" : reader.GetString(1);
        }

        return result;
    }

    private static async Task<Dictionary<string, IndexInfo>> GetExistingIndexesAsync(
        MySqlConnection connection,
        string tableName,
        CancellationToken ct)
    {
        var result = new Dictionary<string, IndexInfo>(StringComparer.OrdinalIgnoreCase);
        var dbName = connection.Database;

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT INDEX_NAME, NON_UNIQUE, COLUMN_NAME
            FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @tbl
            ORDER BY INDEX_NAME, SEQ_IN_INDEX ASC";
        cmd.Parameters.AddWithValue("@db", dbName);
        cmd.Parameters.AddWithValue("@tbl", tableName);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var name = reader.GetString(0);
            var isUnique = reader.GetInt32(1) == 0;
            var colName = reader.GetString(2);
            if (!result.TryGetValue(name, out var info))
            {
                info = new IndexInfo(name, isUnique, new List<string>());
                result[name] = info;
            }
            info.Columns.Add(colName);
        }

        return result;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Thorough column-diff check. Returns true if the existing DB column differs from
    /// the model in any meaningful way (type/size, nullability, auto-increment, default,
    /// on-update-timestamp, charset, comment).
    /// </summary>
    private bool ColumnNeedsModify(ColumnInfo existing, PropertyInfo prop, ColumnAttribute? colAttr)
    {
        // 1. Data type (including size/precision/unsigned). TypeConverter.GetMySqlType
        //    returns strings like "VARCHAR(128)" or "BIGINT UNSIGNED" — MySQL returns
        //    these in COLUMN_TYPE in the same shape (lowercase on some versions).
        var expectedType = BuildExpectedTypeString(prop, colAttr);
        if (!string.Equals(expectedType, existing.ColumnType, StringComparison.OrdinalIgnoreCase))
        {
            _logger?.Debug($"[MySQL2] column `{existing.Name}` type mismatch: model={expectedType}, db={existing.ColumnType}");
            return true;
        }

        // 2. Nullability
        var expectNullable = IsNullableType(prop.PropertyType) && colAttr?.Primary != true && colAttr?.NotNull != true;
        if (existing.IsNullable != expectNullable)
        {
            _logger?.Debug($"[MySQL2] column `{existing.Name}` nullability mismatch: model={expectNullable}, db={existing.IsNullable}");
            return true;
        }

        // 3. AutoIncrement
        var expectAutoInc = colAttr?.AutoIncrement == true;
        var dbAutoInc = existing.Extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase);
        if (expectAutoInc != dbAutoInc)
        {
            _logger?.Debug($"[MySQL2] column `{existing.Name}` auto_increment mismatch: model={expectAutoInc}, db={dbAutoInc}");
            return true;
        }

        // 4. Default value — normalized
        var modelDefault = NormalizeDefault(ModelDefaultSql(prop, colAttr), expectNullable);
        var dbDefault = NormalizeDefault(existing.DefaultValue, existing.IsNullable);
        // Treat CURRENT_TIMESTAMP variants as equivalent
        var bothCurrentTs =
            modelDefault.StartsWith("CURRENT_TIMESTAMP", StringComparison.OrdinalIgnoreCase) &&
            dbDefault.StartsWith("CURRENT_TIMESTAMP", StringComparison.OrdinalIgnoreCase);
        if (!bothCurrentTs && !string.Equals(modelDefault, dbDefault, StringComparison.OrdinalIgnoreCase))
        {
            _logger?.Debug($"[MySQL2] column `{existing.Name}` default mismatch: model='{modelDefault}', db='{dbDefault}'");
            return true;
        }

        // 5. ON UPDATE CURRENT_TIMESTAMP
        var expectOnUpdate = colAttr?.OnUpdateCurrentTimestamp == true;
        var dbOnUpdate = existing.Extra.Contains("on update CURRENT_TIMESTAMP", StringComparison.OrdinalIgnoreCase);
        if (expectOnUpdate != dbOnUpdate)
        {
            _logger?.Debug($"[MySQL2] column `{existing.Name}` on-update mismatch: model={expectOnUpdate}, db={dbOnUpdate}");
            return true;
        }

        // 6. Charset (only for string types; only if the model explicitly sets one)
        if (!string.IsNullOrWhiteSpace(colAttr?.Charset) &&
            IsStringType(TypeConverter.ResolveColumn(colAttr!, prop.PropertyType).DataType))
        {
            var dbCharset = existing.CharacterSet ?? "";
            if (!string.Equals(colAttr!.Charset, dbCharset, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.Debug($"[MySQL2] column `{existing.Name}` charset mismatch: model={colAttr.Charset}, db={dbCharset}");
                return true;
            }
        }

        // 7. Comment
        var modelComment = colAttr?.Comment ?? "";
        var dbComment = existing.Comment ?? "";
        if (!string.Equals(modelComment, dbComment, StringComparison.Ordinal))
        {
            _logger?.Debug($"[MySQL2] column `{existing.Name}` comment mismatch");
            return true;
        }

        return false;
    }

    private static string BuildExpectedTypeString(PropertyInfo prop, ColumnAttribute? colAttr)
    {
        var effective = colAttr ?? new ColumnAttribute();
        return TypeConverter.GetMySqlType(effective, effective.StorageType, prop.PropertyType);
    }

    /// <summary>
    /// The SQL default clause value for a column, or null for none. <see cref="ColumnAttribute.DefaultValue"/>
    /// is raw SQL (<c>0</c>, <c>CURRENT_TIMESTAMP</c>, <c>'abc'</c>); an empty string means an
    /// empty-string default on CHAR/VARCHAR columns (written <c>''</c>) and is ignored on other
    /// types, where MySQL allows no literal empty default (TEXT/BLOB/JSON) or it has no meaning.
    /// </summary>
    internal static string? ModelDefaultSql(PropertyInfo prop, ColumnAttribute? colAttr)
    {
        var value = colAttr?.DefaultValue;
        if (value is null)
            return null;
        if (value.Length > 0)
            return value;
        var type = colAttr!.DataType == DataType.Unspecified && prop.PropertyType == typeof(string)
            ? DataType.VarChar
            : colAttr.DataType;
        return type is DataType.VarChar or DataType.Char ? "''" : null;
    }

    /// <summary>
    /// Normalizes a default for comparison. A NOT NULL column without a default is kept apart
    /// from one whose default is the empty string, so sync adds or drops <c>DEFAULT ''</c>.
    /// </summary>
    internal static string NormalizeDefault(string? value, bool isNullable)
    {
        if (value is null)
            return isNullable ? "NULL" : NoDefault;
        var trimmed = value.Trim();
        // Strip surrounding quotes that MySQL sometimes returns for string defaults
        if (trimmed.Length >= 2 && trimmed.StartsWith('\'') && trimmed.EndsWith('\''))
            trimmed = trimmed[1..^1];
        return trimmed.ToUpperInvariant();
    }

    private const string NoDefault = "\u0000no-default";

    private static bool IsStringType(DataType? type) =>
        type is DataType.VarChar or DataType.Char or DataType.Text or
                 DataType.TinyText or DataType.MediumText or DataType.LongText;

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

    private static string CharsetToString(Charset charset) => charset switch
    {
        Charset.Utf8mb4 => "utf8mb4",
        Charset.Utf8    => "utf8",
        Charset.Latin1  => "latin1",
        Charset.Ascii   => "ascii",
        Charset.Binary  => "binary",
        _               => "utf8mb4"
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

    private record IndexInfo(string Name, bool IsUnique, List<string> Columns);
}
