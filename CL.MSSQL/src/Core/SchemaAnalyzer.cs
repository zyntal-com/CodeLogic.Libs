using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using CL.MSSQL.Models;
using CodeLogic.Core.Logging;
using Microsoft.Data.SqlClient;

namespace CL.MSSQL.Core;

internal sealed class SchemaAnalyzer
{
    private readonly ILogger? _logger;
    public SchemaAnalyzer(ILogger? logger = null) => _logger = logger;

    public static string GetTableName(Type entityType) => entityType.GetCustomAttribute<TableAttribute>()?.Name ?? entityType.Name;
    public static string GetSchemaName(Type entityType) => entityType.GetCustomAttribute<TableAttribute>()?.Schema ?? "dbo";
    private static string Qualified(Type type) => SqlServerDialect.Qualify(GetSchemaName(type), GetTableName(type));

    /// <param name="entityType">The entity whose table to emit.</param>
    /// <param name="defaultStringSize">
    /// <c>nvarchar</c> length used for a string column with no explicit <c>[Column(Size)]</c>,
    /// from <c>SqlServerDatabaseConfig.DefaultStringSize</c>.
    /// </param>
    public string GenerateCreateTable(Type entityType, int defaultStringSize = 255)
    {
        var schema = GetSchemaName(entityType);
        var table = GetTableName(entityType);
        var properties = MappedProperties(entityType);
        var definitions = properties.Select(property => BuildColumnDef(property, defaultStringSize)).ToList();
        var pk = properties.Where(p => p.GetCustomAttribute<ColumnAttribute>()?.Primary == true).ToArray();
        if (pk.Length > 0)
            definitions.Add($"CONSTRAINT {SqlServerDialect.Quote($"PK_{table}")} PRIMARY KEY ({string.Join(", ", pk.Select(p => SqlServerDialect.Quote(ColumnName(p))))})");

        foreach (var property in properties)
        {
            var column = property.GetCustomAttribute<ColumnAttribute>();
            var name = ColumnName(property);
            if (column?.Unique == true && !column.Primary)
                definitions.Add($"CONSTRAINT {SqlServerDialect.Quote($"UQ_{table}_{name}")} UNIQUE ({SqlServerDialect.Quote(name)})");
            if (column?.DataType == DataType.Json)
                definitions.Add($"CONSTRAINT {SqlServerDialect.Quote($"CK_{table}_{name}_ISJSON")} CHECK ({SqlServerDialect.Quote(name)} IS NULL OR ISJSON({SqlServerDialect.Quote(name)}) = 1)");
            if (property.GetCustomAttribute<ForeignKeyAttribute>() is { } fk)
            {
                var constraint = fk.ConstraintName ?? $"FK_{table}_{name}_{fk.ReferenceTable}";
                definitions.Add($"CONSTRAINT {SqlServerDialect.Quote(constraint)} FOREIGN KEY ({SqlServerDialect.Quote(name)}) REFERENCES {QualifyReference(fk.ReferenceTable, schema)} ({SqlServerDialect.Quote(fk.ReferenceColumn)}) ON DELETE {FkAction(fk.OnDelete)} ON UPDATE {FkAction(fk.OnUpdate)}");
            }
        }

        var sql = new StringBuilder();
        sql.AppendLine($"IF SCHEMA_ID(N'{Escape(schema)}') IS NULL EXEC(N'CREATE SCHEMA {SqlServerDialect.Quote(schema)}');");
        sql.Append($"CREATE TABLE {Qualified(entityType)} (\n  {string.Join(",\n  ", definitions)}\n);");
        foreach (var index in IndexStatements(entityType)) sql.AppendLine().Append(index);
        foreach (var comment in CommentStatements(entityType)) sql.AppendLine().Append(comment);
        return sql.ToString();
    }

    public string ComputeSchemaCrc(Type entityType, int defaultStringSize = 255) =>
        ComputeCrc(NormalizeForCrc(GenerateCreateTable(entityType, defaultStringSize)));
    public static string ComputeCrc(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..8];
    internal static string NormalizeForCrc(string ddl) => string.Join(' ', ddl.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    public async Task<List<string>> GenerateAlterStatementsAsync(Type entityType, SqlConnection connection,
        SchemaSyncLevel level = SchemaSyncLevel.Safe, CancellationToken ct = default, int defaultStringSize = 255)
    {
        var result = new List<string>();
        if (level == SchemaSyncLevel.None) return result;
        var schema = GetSchemaName(entityType);
        var table = GetTableName(entityType);
        var qualified = Qualified(entityType);
        var columns = await ReadColumns(connection, schema, table, ct).ConfigureAwait(false);
        var indexDefinitions = await ReadIndexes(connection, schema, table, qualified, ct).ConfigureAwait(false);
        var indexes = indexDefinitions.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var foreignKeyDefinitions = await ReadForeignKeys(connection, schema, table, qualified, ct).ConfigureAwait(false);
        var foreignKeys = foreignKeyDefinitions.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var constraints = await ReadNames(connection,
            "SELECT o.name FROM sys.objects o JOIN sys.tables t ON t.object_id=o.parent_object_id JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE s.name=@schema AND t.name=@table AND o.type IN ('F','D','C','UQ','PK')",
            schema, table, ct).ConfigureAwait(false);
        var comments = await ReadComments(connection, schema, table, ct).ConfigureAwait(false);
        var keyDefinitions = await ReadKeyConstraints(connection, schema, table, ct).ConfigureAwait(false);
        var checkDefinitions = await ReadChecks(connection, schema, table, ct).ConfigureAwait(false);
        var expectedIndexStatements = IndexStatements(entityType)
            .ToDictionary(ExtractIndexName, StringComparer.OrdinalIgnoreCase);
        var droppedIndexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var droppedConstraints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var modelColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var modelForeignKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // previous → new column name; sp_rename carries indexes along, so they are not drift.
        var renamedColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in MappedProperties(entityType))
        {
            var name = ColumnName(property);
            var attr = property.GetCustomAttribute<ColumnAttribute>();
            modelColumns.Add(name);
            if (!columns.TryGetValue(name, out var existing))
            {
                if (!string.IsNullOrWhiteSpace(attr?.PreviousName) && columns.Remove(attr.PreviousName, out existing))
                {
                    result.Add($"EXEC sys.sp_rename N'{Escape(schema)}.{Escape(table)}.{Escape(attr.PreviousName)}', N'{Escape(name)}', 'COLUMN';");
                    modelColumns.Add(attr.PreviousName);
                    renamedColumns[attr.PreviousName] = name;
                    existing = existing with { Name = name };
                    columns[name] = existing;
                }
                else
                {
                    result.Add($"ALTER TABLE {qualified} ADD {BuildColumnDef(property, defaultStringSize)};");
                    existing = null;
                }
            }

            if (existing is not null)
            {
                var expectedType = NormalizeType(TypeConverter.GetSqlServerType(
                    attr ?? TypeConverter.InferColumn(property.PropertyType, defaultStringSize),
                    attr?.StorageType ?? StorageType.Default,
                    property.PropertyType));
                var expectedNullable = IsNullable(property, attr);
                var expectedIdentity = attr?.AutoIncrement == true;
                var expectedCollation = string.IsNullOrWhiteSpace(attr?.Collation) ? existing.Collation : attr!.Collation;
                var structuralMismatch = !string.Equals(existing.TypeDeclaration, expectedType, StringComparison.OrdinalIgnoreCase)
                    || existing.Nullable != expectedNullable
                    || existing.Identity != expectedIdentity
                    || !string.Equals(existing.Collation, expectedCollation, StringComparison.OrdinalIgnoreCase);

                if (structuralMismatch)
                {
                    var replacementRequired = existing.Identity != expectedIdentity
                        || existing.TypeDeclaration is "rowversion" or "timestamp"
                        || expectedType is "rowversion" or "timestamp";
                    var safe = !replacementRequired && IsSafeColumnChange(existing, expectedType, expectedNullable, expectedCollation);
                    if (level >= SchemaSyncLevel.Full || safe)
                    {
                        // SQL Server refuses ALTER COLUMN while defaults, keys, checks,
                        // foreign keys, or indexes depend on it. Drop only objects that
                        // belong to this model and recreate them after all column work.
                        if (existing.DefaultConstraint is not null && droppedConstraints.Add(existing.DefaultConstraint))
                        {
                            result.Add($"ALTER TABLE {qualified} DROP CONSTRAINT {SqlServerDialect.Quote(existing.DefaultConstraint)};");
                            existing = existing with { DefaultConstraint = null, DefaultDefinition = null };
                            columns[name] = existing;
                        }
                        foreach (var (indexName, statement) in expectedIndexStatements)
                        {
                            if (indexes.Contains(indexName) && statement.Contains(SqlServerDialect.Quote(name), StringComparison.OrdinalIgnoreCase)
                                && droppedIndexes.Add(indexName))
                                result.Add($"DROP INDEX {SqlServerDialect.Quote(indexName)} ON {qualified};");
                        }
                        var managedConstraints = ManagedColumnConstraints(entityType, property);
                        foreach (var constraintName in managedConstraints)
                        {
                            if (constraints.Contains(constraintName) && droppedConstraints.Add(constraintName))
                                result.Add($"ALTER TABLE {qualified} DROP CONSTRAINT {SqlServerDialect.Quote(constraintName)};");
                        }

                        if (replacementRequired)
                        {
                            result.Add($"ALTER TABLE {qualified} DROP COLUMN {SqlServerDialect.Quote(name)};");
                            result.Add($"ALTER TABLE {qualified} ADD {BuildColumnDef(property, defaultStringSize)};");
                        }
                        else
                        {
                            var collation = expectedCollation is null ? "" : $" COLLATE {ValidateCollation(expectedCollation)}";
                            result.Add($"ALTER TABLE {qualified} ALTER COLUMN {SqlServerDialect.Quote(name)} {expectedType}{collation} {(expectedNullable ? "NULL" : "NOT NULL")};");
                        }
                    }
                }

                var expectedDefault = NormalizeDefault(attr?.DataType == DataType.RowVersion ? null : attr?.DefaultValue);
                if (!string.Equals(NormalizeDefault(existing.DefaultDefinition), expectedDefault, StringComparison.OrdinalIgnoreCase))
                {
                    if (existing.DefaultConstraint is not null && droppedConstraints.Add(existing.DefaultConstraint))
                        result.Add($"ALTER TABLE {qualified} DROP CONSTRAINT {SqlServerDialect.Quote(existing.DefaultConstraint)};");
                    if (expectedDefault is not null)
                        result.Add($"ALTER TABLE {qualified} ADD CONSTRAINT {SqlServerDialect.Quote($"DF_{table}_{name}")} DEFAULT ({attr!.DefaultValue}) FOR {SqlServerDialect.Quote(name)};");
                }
            }
            if (attr?.DataType == DataType.Json)
            {
                var ck = $"CK_{table}_{name}_ISJSON";
                var expectedCheck = $"{SqlServerDialect.Quote(name)} IS NULL OR ISJSON({SqlServerDialect.Quote(name)}) = 1";
                if (checkDefinitions.TryGetValue(ck, out var catalogCheck)
                    && !NormalizeCheck(catalogCheck).Equals(NormalizeCheck(expectedCheck), StringComparison.OrdinalIgnoreCase)
                    && droppedConstraints.Add(ck))
                    result.Add($"ALTER TABLE {qualified} DROP CONSTRAINT {SqlServerDialect.Quote(ck)};");
                if (!constraints.Contains(ck) || droppedConstraints.Contains(ck)) result.Add($"ALTER TABLE {qualified} ADD CONSTRAINT {SqlServerDialect.Quote(ck)} CHECK ({SqlServerDialect.Quote(name)} IS NULL OR ISJSON({SqlServerDialect.Quote(name)}) = 1);");
            }
            if (property.GetCustomAttribute<ForeignKeyAttribute>() is { } fk)
            {
                var fkName = fk.ConstraintName ?? $"FK_{table}_{name}_{fk.ReferenceTable}";
                modelForeignKeys.Add(fkName);
                var fkStatement = $"ALTER TABLE {qualified} ADD CONSTRAINT {SqlServerDialect.Quote(fkName)} FOREIGN KEY ({SqlServerDialect.Quote(name)}) REFERENCES {QualifyReference(fk.ReferenceTable, schema)} ({SqlServerDialect.Quote(fk.ReferenceColumn)}) ON DELETE {FkAction(fk.OnDelete)} ON UPDATE {FkAction(fk.OnUpdate)};";
                if (foreignKeyDefinitions.TryGetValue(fkName, out var catalogFk)
                    && !NormalizeSql(catalogFk).Equals(NormalizeSql(fkStatement), StringComparison.OrdinalIgnoreCase)
                    && droppedConstraints.Add(fkName))
                    result.Add($"ALTER TABLE {qualified} DROP CONSTRAINT {SqlServerDialect.Quote(fkName)};");
                if (!constraints.Contains(fkName) || droppedConstraints.Contains(fkName))
                    result.Add(fkStatement);
            }
        }

        var primaryColumns = MappedProperties(entityType)
            .Where(p => p.GetCustomAttribute<ColumnAttribute>()?.Primary == true).ToArray();
        if (primaryColumns.Length > 0)
        {
            var pkName = $"PK_{table}";
            var expectedColumns = string.Join(", ", primaryColumns.Select(p => SqlServerDialect.Quote(ColumnName(p))));
            if (keyDefinitions.TryGetValue(pkName, out var key)
                && (!key.Primary || !key.Columns.Equals(expectedColumns, StringComparison.OrdinalIgnoreCase))
                && droppedConstraints.Add(pkName))
                result.Add($"ALTER TABLE {qualified} DROP CONSTRAINT {SqlServerDialect.Quote(pkName)};");
            if (!constraints.Contains(pkName) || droppedConstraints.Contains(pkName))
                result.Add($"ALTER TABLE {qualified} ADD CONSTRAINT {SqlServerDialect.Quote(pkName)} PRIMARY KEY ({expectedColumns});");
        }
        foreach (var property in MappedProperties(entityType))
        {
            var attr = property.GetCustomAttribute<ColumnAttribute>();
            if (attr?.Unique != true || attr.Primary) continue;
            var name = ColumnName(property);
            var uniqueName = $"UQ_{table}_{name}";
            var expectedColumns = SqlServerDialect.Quote(name);
            if (keyDefinitions.TryGetValue(uniqueName, out var key)
                && (key.Primary || !key.Columns.Equals(expectedColumns, StringComparison.OrdinalIgnoreCase))
                && droppedConstraints.Add(uniqueName))
                result.Add($"ALTER TABLE {qualified} DROP CONSTRAINT {SqlServerDialect.Quote(uniqueName)};");
            if (!constraints.Contains(uniqueName) || droppedConstraints.Contains(uniqueName))
                result.Add($"ALTER TABLE {qualified} ADD CONSTRAINT {SqlServerDialect.Quote(uniqueName)} UNIQUE ({SqlServerDialect.Quote(name)});");
        }
        var modelIndexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (indexName, statement) in expectedIndexStatements)
        {
            modelIndexes.Add(indexName);
            if (indexDefinitions.TryGetValue(indexName, out var catalogStatement))
                foreach (var (previous, current) in renamedColumns)
                    catalogStatement = catalogStatement.Replace(
                        SqlServerDialect.Quote(previous), SqlServerDialect.Quote(current), StringComparison.OrdinalIgnoreCase);
            var definitionChanged = catalogStatement is not null
                && !NormalizeSql(catalogStatement).Equals(NormalizeSql(statement), StringComparison.OrdinalIgnoreCase);
            if (definitionChanged && !droppedIndexes.Contains(indexName))
            {
                _logger?.Warning(
                    $"[MSSQL] Index [{indexName}] on table [{table}] has drifted. " +
                    $"Existing: {catalogStatement} Model: {statement}");
                // Rebuilding means a DROP, so it waits for a level allowed to drop; Safe leaves the
                // index alone and the Full-level check flags the table DriftPending instead.
                if (level >= SchemaSyncLevel.Additive)
                {
                    result.Add($"DROP INDEX {SqlServerDialect.Quote(indexName)} ON {qualified};");
                    droppedIndexes.Add(indexName);
                }
            }
            if (!indexes.Contains(indexName) || droppedIndexes.Contains(indexName)) result.Add(statement);
        }
        if (level >= SchemaSyncLevel.Additive)
        {
            foreach (var foreignKey in foreignKeys.Where(name => !modelForeignKeys.Contains(name)))
                result.Add($"ALTER TABLE {qualified} DROP CONSTRAINT {SqlServerDialect.Quote(foreignKey)};");
            foreach (var index in indexes.Where(name => !modelIndexes.Contains(name)))
                result.Add($"DROP INDEX {SqlServerDialect.Quote(index)} ON {qualified};");
        }
        var expectedComments = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [""] = entityType.GetCustomAttribute<TableAttribute>()?.Comment
        };
        foreach (var property in MappedProperties(entityType))
            expectedComments[ColumnName(property)] = property.GetCustomAttribute<ColumnAttribute>()?.Comment;
        foreach (var (column, expectedComment) in expectedComments)
        {
            comments.TryGetValue(column, out var currentComment);
            if (string.Equals(currentComment, expectedComment, StringComparison.Ordinal)) continue;
            if (expectedComment is null)
            {
                if (currentComment is not null) result.Add(ExtendedPropertyDrop(schema, table, column.Length == 0 ? null : column));
            }
            else
            {
                result.Add(currentComment is null
                    ? ExtendedProperty(expectedComment, schema, table, column.Length == 0 ? null : column)
                    : ExtendedPropertyUpdate(expectedComment, schema, table, column.Length == 0 ? null : column));
            }
        }
        if (level >= SchemaSyncLevel.Full)
        {
            var expectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (primaryColumns.Length > 0) expectedKeys.Add($"PK_{table}");
            foreach (var property in MappedProperties(entityType))
            {
                var attribute = property.GetCustomAttribute<ColumnAttribute>();
                if (attribute?.Unique == true && !attribute.Primary)
                    expectedKeys.Add($"UQ_{table}_{ColumnName(property)}");
            }
            foreach (var keyName in keyDefinitions.Keys.Where(name =>
                         (name.Equals($"PK_{table}", StringComparison.OrdinalIgnoreCase)
                          || name.StartsWith($"UQ_{table}_", StringComparison.OrdinalIgnoreCase))
                         && !expectedKeys.Contains(name)))
                if (droppedConstraints.Add(keyName))
                    result.Add($"ALTER TABLE {qualified} DROP CONSTRAINT {SqlServerDialect.Quote(keyName)};");

            var expectedChecks = MappedProperties(entityType)
                .Where(p => p.GetCustomAttribute<ColumnAttribute>()?.DataType == DataType.Json)
                .Select(p => $"CK_{table}_{ColumnName(p)}_ISJSON")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var checkName in checkDefinitions.Keys.Where(name =>
                         name.StartsWith($"CK_{table}_", StringComparison.OrdinalIgnoreCase)
                         && name.EndsWith("_ISJSON", StringComparison.OrdinalIgnoreCase)
                         && !expectedChecks.Contains(name)))
                if (droppedConstraints.Add(checkName))
                    result.Add($"ALTER TABLE {qualified} DROP CONSTRAINT {SqlServerDialect.Quote(checkName)};");

            foreach (var column in columns.Keys.Where(c => !modelColumns.Contains(c)))
            {
                if (columns[column].DefaultConstraint is { } defaultName && droppedConstraints.Add(defaultName))
                    result.Add($"ALTER TABLE {qualified} DROP CONSTRAINT {SqlServerDialect.Quote(defaultName)};");
                foreach (var (keyName, key) in keyDefinitions)
                    if (key.Columns.Split(',').Any(c => c.Trim().Equals(SqlServerDialect.Quote(column), StringComparison.OrdinalIgnoreCase))
                        && droppedConstraints.Add(keyName))
                        result.Add($"ALTER TABLE {qualified} DROP CONSTRAINT {SqlServerDialect.Quote(keyName)};");
                foreach (var (checkName, definition) in checkDefinitions)
                    if (definition.Contains(SqlServerDialect.Quote(column), StringComparison.OrdinalIgnoreCase)
                        && droppedConstraints.Add(checkName))
                        result.Add($"ALTER TABLE {qualified} DROP CONSTRAINT {SqlServerDialect.Quote(checkName)};");
                result.Add($"ALTER TABLE {qualified} DROP COLUMN {SqlServerDialect.Quote(column)};");
            }
        }
        return result;
    }

    private static string BuildColumnDef(PropertyInfo property, int defaultStringSize = 255)
    {
        var attr = property.GetCustomAttribute<ColumnAttribute>()
                   ?? TypeConverter.InferColumn(property.PropertyType, defaultStringSize);
        var nullable = IsNullable(property, attr);
        var parts = new List<string> { SqlServerDialect.Quote(ColumnName(property)), TypeConverter.GetSqlServerType(attr, attr.StorageType, property.PropertyType) };
        if (!string.IsNullOrWhiteSpace(attr.Collation)) parts.Add($"COLLATE {ValidateCollation(attr.Collation)}");
        if (attr.AutoIncrement) parts.Add("IDENTITY(1,1)");
        if (attr.DataType != DataType.RowVersion) parts.Add(nullable ? "NULL" : "NOT NULL");
        if (!string.IsNullOrWhiteSpace(attr.DefaultValue) && attr.DataType != DataType.RowVersion)
            parts.Add($"CONSTRAINT {SqlServerDialect.Quote($"DF_{GetTableName(property.DeclaringType!)}_{ColumnName(property)}")} DEFAULT ({attr.DefaultValue})");
        return string.Join(' ', parts);
    }

    private static bool IsNullable(PropertyInfo property, ColumnAttribute? attribute) =>
        attribute?.DataType != DataType.RowVersion &&
        (!(attribute?.NotNull ?? false) && !(attribute?.Primary ?? false)
            && (!property.PropertyType.IsValueType || Nullable.GetUnderlyingType(property.PropertyType) is not null));

    private static IEnumerable<string> ManagedColumnConstraints(Type entityType, PropertyInfo property)
    {
        var table = GetTableName(entityType);
        var column = ColumnName(property);
        var attribute = property.GetCustomAttribute<ColumnAttribute>();
        if (attribute?.Primary == true) yield return $"PK_{table}";
        if (attribute?.Unique == true && !attribute.Primary) yield return $"UQ_{table}_{column}";
        if (attribute?.DataType == DataType.Json) yield return $"CK_{table}_{column}_ISJSON";
        if (property.GetCustomAttribute<ForeignKeyAttribute>() is { } fk)
            yield return fk.ConstraintName ?? $"FK_{table}_{column}_{fk.ReferenceTable}";
    }

    private static async Task<Dictionary<string, CatalogColumn>> ReadColumns(
        SqlConnection connection, string schema, string table, CancellationToken ct)
    {
        const string sql = """
            SELECT c.name, ty.name, c.max_length, c.precision, c.scale, c.is_nullable,
                   c.is_identity, c.collation_name, dc.name, dc.definition
            FROM sys.columns c
            JOIN sys.types ty ON ty.user_type_id=c.user_type_id
            JOIN sys.tables t ON t.object_id=c.object_id
            JOIN sys.schemas s ON s.schema_id=t.schema_id
            LEFT JOIN sys.default_constraints dc ON dc.object_id=c.default_object_id
            WHERE s.name=@schema AND t.name=@table
            ORDER BY c.column_id
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(new SqlParameter("@schema", System.Data.SqlDbType.NVarChar, 128) { Value = schema });
        command.Parameters.Add(new SqlParameter("@table", System.Data.SqlDbType.NVarChar, 128) { Value = table });
        var columns = new Dictionary<string, CatalogColumn>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var type = reader.GetString(1).ToLowerInvariant();
            var maxLength = reader.GetInt16(2);
            var precision = reader.GetByte(3);
            var scale = reader.GetByte(4);
            var declaration = type switch
            {
                "varchar" or "char" or "varbinary" or "binary" => $"{type}({(maxLength == -1 ? "max" : maxLength)})",
                "nvarchar" or "nchar" => $"{type}({(maxLength == -1 ? "max" : maxLength / 2)})",
                "decimal" or "numeric" => $"{type}({precision},{scale})",
                "datetime2" or "datetimeoffset" or "time" => $"{type}({scale})",
                _ => type
            };
            var column = new CatalogColumn(
                reader.GetString(0), NormalizeType(declaration), reader.GetBoolean(5), reader.GetBoolean(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9));
            columns[column.Name] = column;
        }
        return columns;
    }

    private static async Task<Dictionary<string, string>> ReadIndexes(
        SqlConnection connection, string schema, string table, string qualified, CancellationToken ct)
    {
        const string sql = """
            SELECT i.name, i.is_unique,
                   STRING_AGG(CASE WHEN ic.key_ordinal>0 THEN QUOTENAME(c.name) END, ', ') WITHIN GROUP (ORDER BY ic.index_column_id),
                   STRING_AGG(CASE WHEN ic.is_included_column=1 THEN QUOTENAME(c.name) END, ', ') WITHIN GROUP (ORDER BY ic.index_column_id)
            FROM sys.indexes i
            JOIN sys.tables t ON t.object_id=i.object_id
            JOIN sys.schemas s ON s.schema_id=t.schema_id
            JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
            JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
            WHERE s.name=@schema AND t.name=@table AND i.name IS NOT NULL
              AND i.is_primary_key=0 AND i.is_unique_constraint=0 AND i.is_hypothetical=0
            GROUP BY i.name, i.is_unique, i.index_id
            ORDER BY i.index_id
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(new SqlParameter("@schema", System.Data.SqlDbType.NVarChar, 128) { Value = schema });
        command.Parameters.Add(new SqlParameter("@table", System.Data.SqlDbType.NVarChar, 128) { Value = table });
        var indexes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var name = reader.GetString(0);
            var keys = reader.IsDBNull(2) ? "" : reader.GetString(2);
            var includes = reader.IsDBNull(3) ? "" : reader.GetString(3);
            indexes[name] = $"CREATE {(reader.GetBoolean(1) ? "UNIQUE " : "")}INDEX {SqlServerDialect.Quote(name)} ON {qualified} ({keys}){(includes.Length == 0 ? "" : $" INCLUDE ({includes})")};";
        }
        return indexes;
    }

    private static async Task<Dictionary<string, string>> ReadForeignKeys(
        SqlConnection connection, string schema, string table, string qualified, CancellationToken ct)
    {
        const string sql = """
            SELECT fk.name, pc.name, rs.name, rt.name, rc.name,
                   fk.delete_referential_action_desc, fk.update_referential_action_desc
            FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id=fk.object_id
            JOIN sys.tables pt ON pt.object_id=fk.parent_object_id
            JOIN sys.schemas ps ON ps.schema_id=pt.schema_id
            JOIN sys.columns pc ON pc.object_id=pt.object_id AND pc.column_id=fkc.parent_column_id
            JOIN sys.tables rt ON rt.object_id=fk.referenced_object_id
            JOIN sys.schemas rs ON rs.schema_id=rt.schema_id
            JOIN sys.columns rc ON rc.object_id=rt.object_id AND rc.column_id=fkc.referenced_column_id
            WHERE ps.name=@schema AND pt.name=@table
            ORDER BY fk.name, fkc.constraint_column_id
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(new SqlParameter("@schema", System.Data.SqlDbType.NVarChar, 128) { Value = schema });
        command.Parameters.Add(new SqlParameter("@table", System.Data.SqlDbType.NVarChar, 128) { Value = table });
        var foreignKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var name = reader.GetString(0);
            // Attribute mappings currently describe one local/reference column per FK.
            foreignKeys[name] = $"ALTER TABLE {qualified} ADD CONSTRAINT {SqlServerDialect.Quote(name)} FOREIGN KEY ({SqlServerDialect.Quote(reader.GetString(1))}) REFERENCES {SqlServerDialect.Qualify(reader.GetString(2), reader.GetString(3))} ({SqlServerDialect.Quote(reader.GetString(4))}) ON DELETE {reader.GetString(5).Replace('_', ' ')} ON UPDATE {reader.GetString(6).Replace('_', ' ')};";
        }
        return foreignKeys;
    }

    private static async Task<Dictionary<string, string>> ReadComments(
        SqlConnection connection, string schema, string table, CancellationToken ct)
    {
        const string sql = """
            SELECT ISNULL(c.name, N''), CONVERT(nvarchar(max), ep.value)
            FROM sys.extended_properties ep
            JOIN sys.tables t ON t.object_id=ep.major_id
            JOIN sys.schemas s ON s.schema_id=t.schema_id
            LEFT JOIN sys.columns c ON c.object_id=t.object_id AND c.column_id=ep.minor_id
            WHERE ep.name=N'MS_Description' AND s.name=@schema AND t.name=@table
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(new SqlParameter("@schema", System.Data.SqlDbType.NVarChar, 128) { Value = schema });
        command.Parameters.Add(new SqlParameter("@table", System.Data.SqlDbType.NVarChar, 128) { Value = table });
        var comments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) comments[reader.GetString(0)] = reader.GetString(1);
        return comments;
    }

    private static async Task<Dictionary<string, CatalogKey>> ReadKeyConstraints(
        SqlConnection connection, string schema, string table, CancellationToken ct)
    {
        const string sql = """
            SELECT kc.name, CONVERT(bit, CASE WHEN kc.type='PK' THEN 1 ELSE 0 END),
                   STRING_AGG(QUOTENAME(c.name), ', ') WITHIN GROUP (ORDER BY ic.key_ordinal)
            FROM sys.key_constraints kc
            JOIN sys.tables t ON t.object_id=kc.parent_object_id
            JOIN sys.schemas s ON s.schema_id=t.schema_id
            JOIN sys.index_columns ic ON ic.object_id=t.object_id AND ic.index_id=kc.unique_index_id AND ic.key_ordinal>0
            JOIN sys.columns c ON c.object_id=t.object_id AND c.column_id=ic.column_id
            WHERE s.name=@schema AND t.name=@table
            GROUP BY kc.name, kc.type
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(new SqlParameter("@schema", System.Data.SqlDbType.NVarChar, 128) { Value = schema });
        command.Parameters.Add(new SqlParameter("@table", System.Data.SqlDbType.NVarChar, 128) { Value = table });
        var keys = new Dictionary<string, CatalogKey>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            keys[reader.GetString(0)] = new CatalogKey(reader.GetBoolean(1), reader.GetString(2));
        return keys;
    }

    private static async Task<Dictionary<string, string>> ReadChecks(
        SqlConnection connection, string schema, string table, CancellationToken ct)
    {
        const string sql = """
            SELECT cc.name, cc.definition
            FROM sys.check_constraints cc
            JOIN sys.tables t ON t.object_id=cc.parent_object_id
            JOIN sys.schemas s ON s.schema_id=t.schema_id
            WHERE s.name=@schema AND t.name=@table
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(new SqlParameter("@schema", System.Data.SqlDbType.NVarChar, 128) { Value = schema });
        command.Parameters.Add(new SqlParameter("@table", System.Data.SqlDbType.NVarChar, 128) { Value = table });
        var checks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) checks[reader.GetString(0)] = reader.GetString(1);
        return checks;
    }

    private static string NormalizeType(string value) =>
        value.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();

    private static string NormalizeSql(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();

    private static string NormalizeCheck(string value) =>
        value.Replace("(", "", StringComparison.Ordinal).Replace(")", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal);

    private static string? NormalizeDefault(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        while (normalized.Length > 1 && normalized[0] == '(' && normalized[^1] == ')')
            normalized = normalized[1..^1].Trim();
        return normalized;
    }

    private static bool IsSafeColumnChange(CatalogColumn current, string expectedType, bool expectedNullable, string? expectedCollation)
    {
        if (!string.Equals(current.Collation, expectedCollation, StringComparison.OrdinalIgnoreCase)) return false;
        if (current.Nullable && !expectedNullable) return false;
        if (string.Equals(current.TypeDeclaration, expectedType, StringComparison.OrdinalIgnoreCase)) return true;

        static (string Type, int Size)? Sized(string declaration)
        {
            var open = declaration.IndexOf('(');
            if (open < 0 || !declaration.EndsWith(')')) return null;
            var type = declaration[..open];
            var sizeText = declaration[(open + 1)..^1];
            return (type, sizeText.Equals("max", StringComparison.OrdinalIgnoreCase) ? int.MaxValue : int.Parse(sizeText));
        }
        var oldSized = Sized(current.TypeDeclaration);
        var newSized = Sized(expectedType);
        return oldSized is not null && newSized is not null
            && oldSized.Value.Type == newSized.Value.Type
            && newSized.Value.Size >= oldSized.Value.Size;
    }

    private sealed record CatalogColumn(string Name, string TypeDeclaration, bool Nullable, bool Identity,
        string? Collation, string? DefaultConstraint, string? DefaultDefinition);
    private sealed record CatalogKey(bool Primary, string Columns);

    private static IEnumerable<string> IndexStatements(Type type)
    {
        var table = GetTableName(type);
        var qualified = Qualified(type);
        foreach (var property in MappedProperties(type))
        {
            var col = ColumnName(property);
            var attr = property.GetCustomAttribute<ColumnAttribute>();
            if (attr?.Index == true && !attr.Primary && !attr.Unique)
                yield return $"CREATE INDEX {SqlServerDialect.Quote($"IX_{table}_{col}")} ON {qualified} ({SqlServerDialect.Quote(col)});";
            foreach (var index in property.GetCustomAttributes<IndexAttribute>())
            {
                var name = index.Name ?? $"{(index.Unique ? "UX" : "IX")}_{table}_{col}";
                var includes = index.Include is { Length: > 0 }
                    ? $" INCLUDE ({string.Join(", ", index.Include.Select(n => SqlServerDialect.Quote(ResolveColumn(type, n))))})" : "";
                yield return $"CREATE {(index.Unique ? "UNIQUE " : "")}INDEX {SqlServerDialect.Quote(name)} ON {qualified} ({SqlServerDialect.Quote(col)}){includes};";
            }
        }
        foreach (var index in type.GetCustomAttributes<CompositeIndexAttribute>())
            yield return $"CREATE {(index.Unique ? "UNIQUE " : "")}INDEX {SqlServerDialect.Quote(index.IndexName)} ON {qualified} ({string.Join(", ", index.ColumnNames.Select(c => SqlServerDialect.Quote(ResolveColumn(type, c))))});";
    }

    private static IEnumerable<string> CommentStatements(Type type)
    {
        var schema = GetSchemaName(type); var table = GetTableName(type);
        if (type.GetCustomAttribute<TableAttribute>()?.Comment is { Length: > 0 } tableComment)
            yield return ExtendedProperty(tableComment, schema, table, null);
        foreach (var property in MappedProperties(type))
            if (property.GetCustomAttribute<ColumnAttribute>()?.Comment is { Length: > 0 } comment)
                yield return ExtendedProperty(comment, schema, table, ColumnName(property));
    }

    private static string ExtendedProperty(string value, string schema, string table, string? column) =>
        $"EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'{Escape(value)}', @level0type=N'SCHEMA', @level0name=N'{Escape(schema)}', @level1type=N'TABLE', @level1name=N'{Escape(table)}'" +
        (column is null ? ";" : $", @level2type=N'COLUMN', @level2name=N'{Escape(column)}';");

    private static string ExtendedPropertyUpdate(string value, string schema, string table, string? column) =>
        $"EXEC sys.sp_updateextendedproperty @name=N'MS_Description', @value=N'{Escape(value)}', @level0type=N'SCHEMA', @level0name=N'{Escape(schema)}', @level1type=N'TABLE', @level1name=N'{Escape(table)}'" +
        (column is null ? ";" : $", @level2type=N'COLUMN', @level2name=N'{Escape(column)}';");

    private static string ExtendedPropertyDrop(string schema, string table, string? column) =>
        $"EXEC sys.sp_dropextendedproperty @name=N'MS_Description', @level0type=N'SCHEMA', @level0name=N'{Escape(schema)}', @level1type=N'TABLE', @level1name=N'{Escape(table)}'" +
        (column is null ? ";" : $", @level2type=N'COLUMN', @level2name=N'{Escape(column)}';");

    private static async Task<HashSet<string>> ReadNames(SqlConnection connection, string sql, string schema, string table, CancellationToken ct)
    {
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(new SqlParameter("@schema", System.Data.SqlDbType.NVarChar, 128) { Value = schema });
        command.Parameters.Add(new SqlParameter("@table", System.Data.SqlDbType.NVarChar, 128) { Value = table });
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) names.Add(reader.GetString(0));
        return names;
    }

    private static PropertyInfo[] MappedProperties(Type type) => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite && p.GetCustomAttribute<IgnoreAttribute>() is null).ToArray();
    private static string ColumnName(PropertyInfo property) => property.GetCustomAttribute<ColumnAttribute>()?.Name ?? property.Name;
    private static string ResolveColumn(Type type, string name) => MappedProperties(type).FirstOrDefault(p => p.Name == name || ColumnName(p) == name) is { } p ? ColumnName(p) : name;
    private static string QualifyReference(string referenceTable, string defaultSchema) =>
        referenceTable.Contains('.') ? SqlServerDialect.QuoteMultipart(referenceTable) : SqlServerDialect.Qualify(defaultSchema, referenceTable);
    private static string ExtractIndexName(string sql) { var marker = "INDEX ["; var start = sql.IndexOf(marker, StringComparison.OrdinalIgnoreCase) + marker.Length; var end = sql.IndexOf(']', start); return sql[start..end].Replace("]]", "]", StringComparison.Ordinal); }
    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);
    private static string ValidateCollation(string value)
    {
        if (value.Any(c => !char.IsLetterOrDigit(c) && c != '_'))
            throw new ArgumentException($"Invalid SQL Server collation name '{value}'.", nameof(value));
        return value;
    }
    private static string FkAction(ForeignKeyAction action) => action switch { ForeignKeyAction.Cascade => "CASCADE", ForeignKeyAction.SetNull => "SET NULL", ForeignKeyAction.SetDefault => "SET DEFAULT", _ => "NO ACTION" };
}
