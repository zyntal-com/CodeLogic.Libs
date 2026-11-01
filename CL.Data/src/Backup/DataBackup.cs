using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CL.Data.Errors;
using CL.Data.Execution;
using CL.Data.Schema;
using CL.Data.Sql;
using CodeLogic.Core.Results;

namespace CL.Data.Backup;

/// <summary>One table in a backup.</summary>
/// <param name="Table">Table name.</param>
/// <param name="Schema">Schema, where the provider has schemas.</param>
/// <param name="File">Rows file, relative to the backup directory.</param>
/// <param name="Rows">Number of rows written.</param>
/// <param name="Definition">The table as it was read from the catalog.</param>
public sealed record BackupTable(string Table, string? Schema, string File, long Rows, TableSchema Definition);

/// <summary>Describes a backup directory (<c>manifest.json</c>).</summary>
/// <param name="FormatVersion">Format version (1).</param>
/// <param name="Provider">Provider that wrote it; restores go to the same provider.</param>
/// <param name="CreatedAt">When it was written.</param>
/// <param name="Tables">Tables, in the order they can be restored (referenced tables first).</param>
public sealed record BackupManifest(int FormatVersion, string Provider, DateTimeOffset CreatedAt, IReadOnlyList<BackupTable> Tables);

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BackupManifest))]
internal sealed partial class BackupJsonContext : JsonSerializerContext;

/// <summary>
/// Logical backups of tables (<c>db.Backup</c>): the table definitions go to <c>manifest.json</c> and the rows to one
/// JSON-lines file per table, streamed so large tables never sit in memory. Restore recreates the tables on the same
/// provider and reinserts the rows, keeping identity values. SQLite also has <see cref="SnapshotAsync"/>, a native copy
/// of the whole file.
/// </summary>
public sealed class DataBackup
{
    /// <summary>Name of the manifest file in a backup directory.</summary>
    public const string ManifestFile = "manifest.json";

    private const int FormatVersion = 1;
    private readonly Database _db;

    internal DataBackup(Database db) => _db = db;

    /// <summary>
    /// Writes <paramref name="tables"/> (default: every table in <paramref name="schema"/>) to
    /// <paramref name="directory"/>, which must be empty or missing.
    /// </summary>
    public async ValueTask<Result<BackupManifest>> BackupAsync(string directory, IReadOnlyList<string>? tables = null, string? schema = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (_db.Provider.Introspector is not { } introspector) return DataErrors.Unsupported($"{_db.Provider.Name} has no schema introspector.");
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            return DataErrors.Validation($"Backup directory '{directory}' is not empty.");

        if (tables is null)
        {
            var listed = await introspector.ListTablesAsync(_db, schema, ct).ConfigureAwait(false);
            if (listed.IsFailure) return listed.Error!;
            tables = listed.Value!;
        }

        List<TableSchema> definitions = [];
        foreach (var table in tables)
        {
            var read = await introspector.ReadTableAsync(_db, table, schema, ct).ConfigureAwait(false);
            if (read.IsFailure) return read.Error!;
            if (read.Value is null) return DataErrors.NotFound($"Table '{table}' does not exist.");
            definitions.Add(read.Value);
        }

        Directory.CreateDirectory(directory);
        List<BackupTable> entries = [];
        var index = 0;
        foreach (var definition in DependencyOrder(definitions))
        {
            var file = $"{++index:D3}-{SafeFileName(definition.Name)}.jsonl";
            var written = await WriteRowsAsync(definition, Path.Combine(directory, file), ct).ConfigureAwait(false);
            if (written.IsFailure) return written.Error!;
            entries.Add(new BackupTable(definition.Name, definition.Schema, file, written.Value, definition));
        }

        var manifest = new BackupManifest(FormatVersion, _db.Provider.Name, _db.Options.Time.GetUtcNow(), entries);
        await using (var stream = File.Create(Path.Combine(directory, ManifestFile)))
            await JsonSerializer.SerializeAsync(stream, manifest, BackupJsonContext.Default.BackupManifest, ct).ConfigureAwait(false);
        return manifest;
    }

    /// <summary>
    /// Restores the backup in <paramref name="directory"/>: drops the backed-up tables (children first), recreates
    /// them and reinserts their rows. Each table is filled in one transaction. The manifest is checked first: store types,
    /// collations, defaults and table options must be plain values (a default may only be a literal or a known function
    /// such as <c>CURRENT_TIMESTAMP</c>), and every rows file must lie inside <paramref name="directory"/>; otherwise nothing
    /// is touched and <c>data.validation</c> is returned. Restore only backups you trust: the rows are inserted as they are.
    /// </summary>
    public async ValueTask<Result<BackupManifest>> RestoreAsync(string directory, CancellationToken ct = default)
    {
        var manifestPath = Path.Combine(directory, ManifestFile);
        if (!File.Exists(manifestPath)) return DataErrors.NotFound($"No backup manifest in '{directory}'.");
        BackupManifest? manifest;
        await using (var stream = File.OpenRead(manifestPath))
            manifest = await JsonSerializer.DeserializeAsync(stream, BackupJsonContext.Default.BackupManifest, ct).ConfigureAwait(false);
        if (manifest is null || manifest.FormatVersion != FormatVersion)
            return DataErrors.Validation($"'{manifestPath}' is not a CL.Data backup (format {FormatVersion}).");
        if (!string.Equals(manifest.Provider, _db.Provider.Name, StringComparison.OrdinalIgnoreCase))
            return DataErrors.Unsupported($"The backup was written by {manifest.Provider}; restore it to the same provider.");
        if (manifest.Tables is null) return DataErrors.Validation($"'{manifestPath}' lists no tables.");
        // Types, collations, defaults and options become DDL text: refuse anything but plain values before dropping a table.
        if (BackupValidator.Check(manifest, directory) is { } unsafeManifest) return unsafeManifest;

        var ddl = _db.Provider.Ddl;
        foreach (var entry in manifest.Tables.Reverse())
        {
            var dropped = await _db.ExecuteSqlAsync(SqlText.Raw("DROP TABLE IF EXISTS " + _db.Provider.Dialect.QuoteTable(entry.Table, entry.Schema)), ct).ConfigureAwait(false);
            if (dropped.IsFailure) return dropped.Error!;
        }
        foreach (var entry in manifest.Tables)
        {
            foreach (var statement in ddl.Generate(new CreateTableChange(entry.Definition)))
            {
                var created = await _db.ExecuteSqlAsync(SqlText.Raw(statement), ct).ConfigureAwait(false);
                if (created.IsFailure) return DataErrors.SchemaSyncFailed(entry.Table, statement, created.Error);
            }
            var filled = await ReadRowsAsync(entry, Path.Combine(directory, entry.File), ct).ConfigureAwait(false);
            if (filled.IsFailure) return filled.Error!;
        }
        return manifest;
    }

    /// <summary>A consistent copy of the whole database file (SQLite <c>VACUUM INTO</c>); <c>data.unsupported</c> elsewhere.</summary>
    public async ValueTask<Result> SnapshotAsync(string file, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        if (_db.Provider.SnapshotSql(file) is not { } sql) return DataErrors.Unsupported($"{_db.Provider.Name} has no file snapshots; use BackupAsync.");
        if (File.Exists(file)) return DataErrors.Validation($"'{file}' already exists.");
        var done = await _db.ExecuteSqlAsync(SqlText.Raw(sql), ct).ConfigureAwait(false);
        return done.IsFailure ? done.Error! : Result.Success();
    }

    // ── Writing rows ────────────────────────────────────────────────────────

    private ValueTask<Result<long>> WriteRowsAsync(TableSchema table, string path, CancellationToken ct)
    {
        var dialect = _db.Provider.Dialect;
        var columns = string.Join(", ", table.Columns.Select(c => dialect.QuoteIdentifier(c.Name)));
        var order = table.PrimaryKey.Count > 0 ? " ORDER BY " + string.Join(", ", table.PrimaryKey.Select(dialect.QuoteIdentifier)) : "";
        var spec = new CommandSpec($"SELECT {columns} FROM {dialect.QuoteTable(table.Name, table.Schema)}{order}", [], TimeSpan.FromHours(1));
        return _db.Executor.ExecuteAsync(_db.Primary, spec, path, static async (cmd, file, token) =>
        {
            await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token).ConfigureAwait(false);
            await using var stream = File.Create(file);
            await using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { SkipValidation = true });
            var newline = "\n"u8.ToArray();

            json.WriteStartObject();
            json.WriteStartArray("columns");
            for (var i = 0; i < reader.FieldCount; i++) json.WriteStringValue(reader.GetName(i));
            json.WriteEndArray();
            var types = new string[reader.FieldCount];
            json.WriteStartArray("types");
            for (var i = 0; i < reader.FieldCount; i++) json.WriteStringValue(types[i] = TypeCode(reader.GetFieldType(i)));
            json.WriteEndArray();
            json.WriteEndObject();
            await json.FlushAsync(token).ConfigureAwait(false);
            stream.Write(newline);
            json.Reset();

            long rows = 0;
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                json.WriteStartArray();
                for (var i = 0; i < reader.FieldCount; i++) WriteCell(json, reader.GetValue(i), types[i]);   // each column read once (sequential access)
                json.WriteEndArray();
                await json.FlushAsync(token).ConfigureAwait(false);
                stream.Write(newline);
                json.Reset();
                rows++;
            }
            return rows;
        }, null, null, ct);
    }

    /// <summary>
    /// Writes a value of the column's declared type plainly, and any other type (SQLite stores values of mixed
    /// types in one column) as <c>{"t": type, "v": value}</c>.
    /// </summary>
    private static void WriteCell(Utf8JsonWriter json, object? value, string columnType)
    {
        if (value is null or DBNull || TypeCode(value.GetType()) == columnType)
        {
            WriteValue(json, value);
            return;
        }
        json.WriteStartObject();
        json.WriteString("t", TypeCode(value.GetType()));
        json.WritePropertyName("v");
        WriteValue(json, value);
        json.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter json, object? value)
    {
        switch (value)
        {
            case null or DBNull: json.WriteNullValue(); break;
            case bool b: json.WriteBooleanValue(b); break;
            case byte or sbyte or short or ushort or int or uint or long:
                json.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture)); break;
            case ulong u: json.WriteStringValue(u.ToString(CultureInfo.InvariantCulture)); break;
            case decimal m: json.WriteStringValue(m.ToString(CultureInfo.InvariantCulture)); break;
            case double d: json.WriteStringValue(d.ToString("R", CultureInfo.InvariantCulture)); break;
            case float f: json.WriteStringValue(f.ToString("R", CultureInfo.InvariantCulture)); break;
            case byte[] bytes: json.WriteBase64StringValue(bytes); break;
            case Guid g: json.WriteStringValue(g); break;
            case DateTime dt: json.WriteStringValue(dt.ToString("O", CultureInfo.InvariantCulture)); break;
            case DateTimeOffset dto: json.WriteStringValue(dto.ToString("O", CultureInfo.InvariantCulture)); break;
            case DateOnly date: json.WriteStringValue(date.ToString("O", CultureInfo.InvariantCulture)); break;
            case TimeOnly time: json.WriteStringValue(time.ToString("O", CultureInfo.InvariantCulture)); break;
            case TimeSpan span: json.WriteStringValue(span.ToString("c", CultureInfo.InvariantCulture)); break;
            case var other: json.WriteStringValue(Convert.ToString(other, CultureInfo.InvariantCulture)); break;
        }
    }

    private static string TypeCode(Type type) => type switch
    {
        _ when type == typeof(bool) => "bool",
        _ when type == typeof(byte) => "u8",
        _ when type == typeof(sbyte) => "i8",
        _ when type == typeof(short) => "i16",
        _ when type == typeof(ushort) => "u16",
        _ when type == typeof(int) => "i32",
        _ when type == typeof(uint) => "u32",
        _ when type == typeof(long) => "i64",
        _ when type == typeof(ulong) => "u64",
        _ when type == typeof(decimal) => "decimal",
        _ when type == typeof(double) => "f64",
        _ when type == typeof(float) => "f32",
        _ when type == typeof(byte[]) => "bytes",
        _ when type == typeof(Guid) => "guid",
        _ when type == typeof(DateTime) => "datetime",
        _ when type == typeof(DateTimeOffset) => "datetimeoffset",
        _ when type == typeof(DateOnly) => "date",
        _ when type == typeof(TimeOnly) => "time",
        _ when type == typeof(TimeSpan) => "timespan",
        _ => "string",
    };

    // ── Reading rows ────────────────────────────────────────────────────────

    private async ValueTask<Result> ReadRowsAsync(BackupTable entry, string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return DataErrors.NotFound($"Backup rows file '{path}' is missing.");
        using var lines = new StreamReader(path, Encoding.UTF8);
        var headerLine = await lines.ReadLineAsync(ct).ConfigureAwait(false);
        if (headerLine is null) return DataErrors.Validation($"'{path}' is empty.");
        string[] columns;
        string[] types;
        using (var header = JsonDocument.Parse(headerLine))
        {
            columns = header.RootElement.GetProperty("columns").EnumerateArray().Select(e => e.GetString()!).ToArray();
            types = header.RootElement.GetProperty("types").EnumerateArray().Select(e => e.GetString()!).ToArray();
        }
        if (entry.Rows == 0) return Result.Success();

        var provider = _db.Provider;
        var dialect = provider.Dialect;
        var ddl = provider.Ddl;
        var table = dialect.QuoteTable(entry.Table, entry.Schema);
        var columnList = string.Join(", ", columns.Select(dialect.QuoteIdentifier));
        var perStatement = Math.Clamp((provider.Capabilities.ParameterLimit - 1) / Math.Max(1, columns.Length), 1, 500);

        var begun = await _db.BeginTransactionAsync(null, ct).ConfigureAwait(false);
        if (begun.IsFailure) return begun.Error!;
        await using var tx = begun.Value!;
        var hasIdentity = entry.Definition.Columns.Any(c => c.Identity);
        if (hasIdentity)
            foreach (var statement in ddl.IdentityInsertSetup(entry.Definition))
            {
                var set = await tx.Db.ExecuteSqlAsync(SqlText.Raw(statement), ct).ConfigureAwait(false);
                if (set.IsFailure) return set.Error!;
            }

        List<object?[]> pending = [];
        async ValueTask<Result> FlushAsync()
        {
            if (pending.Count == 0) return Result.Success();
            var sql = new StringBuilder("INSERT INTO ").Append(table).Append(" (").Append(columnList).Append(") VALUES ");
            List<SqlParameterValue> parameters = new(pending.Count * columns.Length);
            for (var r = 0; r < pending.Count; r++)
            {
                if (r > 0) sql.Append(", ");
                sql.Append('(');
                for (var c = 0; c < columns.Length; c++)
                {
                    if (c > 0) sql.Append(", ");
                    sql.Append(dialect.ParameterName(parameters.Count));
                    parameters.Add(SqlParameterValue.ForColumn(pending[r][c], null));
                }
                sql.Append(')');
            }
            pending.Clear();
            var inserted = await tx.Db.ExecuteSpecAsync(new CommandSpec(sql.ToString(), parameters), "insert", entry.Table, ct).ConfigureAwait(false);
            return inserted.IsFailure ? inserted.Error! : Result.Success();
        }

        while (await lines.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0) continue;
            using var row = JsonDocument.Parse(line);
            var values = new object?[columns.Length];
            var c = 0;
            foreach (var element in row.RootElement.EnumerateArray())
            {
                if (c >= columns.Length) break;
                var parsed = Parse(element, types[c]);
                if (parsed.IsFailure) return parsed.Error!;
                values[c++] = parsed.Value;
            }
            pending.Add(values);
            if (pending.Count >= perStatement)
            {
                var flushed = await FlushAsync().ConfigureAwait(false);
                if (flushed.IsFailure) return flushed.Error!;
            }
        }
        var last = await FlushAsync().ConfigureAwait(false);
        if (last.IsFailure) return last.Error!;

        if (hasIdentity)
            foreach (var statement in ddl.IdentityInsertTeardown(entry.Definition))
            {
                var reset = await tx.Db.ExecuteSqlAsync(SqlText.Raw(statement), ct).ConfigureAwait(false);
                if (reset.IsFailure) return reset.Error!;
            }
        return await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private static Result<object?> Parse(JsonElement element, string type)
    {
        if (element.ValueKind == JsonValueKind.Null) return Result<object?>.Success(null);
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("t", out var tag) && element.TryGetProperty("v", out var tagged))
            return Parse(tagged, tag.GetString() ?? "string");
        try
        {
            object? value = type switch
            {
                "bool" => element.GetBoolean(),
                "u8" => (byte)element.GetInt64(),
                "i8" => (sbyte)element.GetInt64(),
                "i16" => (short)element.GetInt64(),
                "u16" => (ushort)element.GetInt64(),
                "i32" => (int)element.GetInt64(),
                "u32" => (uint)element.GetInt64(),
                "i64" => element.GetInt64(),
                "u64" => ulong.Parse(element.GetString()!, CultureInfo.InvariantCulture),
                "decimal" => decimal.Parse(element.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture),
                "f64" => double.Parse(element.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture),
                "f32" => float.Parse(element.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture),
                "bytes" => element.GetBytesFromBase64(),
                "guid" => element.GetGuid(),
                "datetime" => DateTime.Parse(element.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                "datetimeoffset" => DateTimeOffset.Parse(element.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                "date" => DateOnly.Parse(element.GetString()!, CultureInfo.InvariantCulture),
                "time" => TimeOnly.Parse(element.GetString()!, CultureInfo.InvariantCulture),
                "timespan" => TimeSpan.ParseExact(element.GetString()!, "c", CultureInfo.InvariantCulture),
                _ => element.GetString(),
            };
            return Result<object?>.Success(value);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or OverflowException)
        {
            return DataErrors.Validation($"Backup value '{element}' is not a valid {type}.", ex.Message);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>Referenced tables first (foreign keys among the backed-up tables).</summary>
    private static List<TableSchema> DependencyOrder(List<TableSchema> tables)
    {
        var byName = tables.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        List<TableSchema> ordered = [];
        HashSet<string> visiting = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> done = new(StringComparer.OrdinalIgnoreCase);
        void Visit(TableSchema table)
        {
            if (done.Contains(table.Name) || !visiting.Add(table.Name)) return;
            foreach (var fk in table.ForeignKeys)
                if (byName.TryGetValue(fk.ReferencedTable, out var referenced) && !ReferenceEquals(referenced, table)) Visit(referenced);
            visiting.Remove(table.Name);
            done.Add(table.Name);
            ordered.Add(table);
        }
        foreach (var table in tables) Visit(table);
        return ordered;
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
    }
}
