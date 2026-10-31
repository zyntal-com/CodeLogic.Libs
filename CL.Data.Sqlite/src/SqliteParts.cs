using System.Data.Common;
using System.Globalization;
using CL.Data.Configuration;
using CL.Data.Errors;
using CL.Data.Execution;
using CL.Data.Mapping;
using CL.Data.Providers;
using CL.Data.Sql;
using Microsoft.Data.Sqlite;

namespace CL.Data.Sqlite;

/// <summary>SQLite SQL dialect: double-quoted identifiers, <c>@p</c> parameters, backslash LIKE escape.</summary>
public sealed class SqliteSqlDialect : SqlDialect;

/// <summary>
/// Pooled SQLite connections with per-connection PRAGMAs (foreign keys, synchronous, busy timeout). For
/// <c>:memory:</c> databases it keeps one connection open so the shared in-memory database lives as long as
/// the data source.
/// </summary>
internal sealed class SqliteDataSource : InitializingDataSource
{
    private readonly string _pragmas;
    private readonly string? _journalPragma;
    private readonly SqliteConnection? _keepAlive;
    private int _journalApplied;

    public SqliteDataSource(string connectionString, SqliteOptions options, bool inMemory) : base(connectionString)
    {
        _pragmas = string.Create(CultureInfo.InvariantCulture,
            $"PRAGMA foreign_keys={(options.ForeignKeys ? "ON" : "OFF")}; PRAGMA synchronous={options.Synchronous.ToString().ToUpperInvariant()}; PRAGMA busy_timeout={options.BusyTimeoutMs};");
        _journalPragma = inMemory || options.ReadOnly ? null : $"PRAGMA journal_mode={options.JournalMode.ToString().ToUpperInvariant()};";
        if (inMemory)
        {
            _keepAlive = new SqliteConnection(connectionString);
            _keepAlive.Open();
        }
    }

    protected override DbConnection CreateDriverConnection() => new SqliteConnection(ConnectionString);

    protected override async ValueTask InitializeAsync(DbConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        // journal_mode is a property of the database file; set it once per data source.
        command.CommandText = _journalPragma is not null && Interlocked.Exchange(ref _journalApplied, 1) == 0
            ? _journalPragma + " " + _pragmas
            : _pragmas;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    protected override void Initialize(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = _journalPragma is not null && Interlocked.Exchange(ref _journalApplied, 1) == 0
            ? _journalPragma + " " + _pragmas
            : _pragmas;
        command.ExecuteNonQuery();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _keepAlive?.Dispose();
        base.Dispose(disposing);
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        if (_keepAlive is not null) await _keepAlive.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsyncCore().ConfigureAwait(false);
    }
}

/// <summary>
/// SQLite type mapping. Integers and booleans are INTEGER, decimals NUMERIC (stored as numbers so they sort
/// numerically), GUIDs 16-byte BLOBs, and date/time values UTC ISO-8601 TEXT (so text order is time order).
/// </summary>
public sealed class SqliteTypeMapper : ITypeMapper
{
    /// <inheritdoc />
    public string GetStoreType(ColumnMap column, DataConnectionConfig config)
    {
        if (column.Hint<SqliteColumnAttribute>()?.Type is { } hinted) return hinted;
        if (column.NativeType is { } native) return native;
        return column.DataType switch
        {
            DataType.Boolean or DataType.TinyInt or DataType.SmallInt or DataType.Int or DataType.BigInt => "INTEGER",
            DataType.Decimal => "NUMERIC",
            DataType.Real or DataType.Double => "REAL",
            DataType.Binary or DataType.FixedBinary or DataType.Blob or DataType.Guid or DataType.RowVersion => "BLOB",
            _ => "TEXT",
        };
    }

    /// <inheritdoc />
    public DbParameter CreateParameter<TValue>(string name, TValue value, ColumnMap? column) =>
        new SqliteParameter(name, ToStore(value));

    private static object ToStore<TValue>(TValue value) => value switch
    {
        null => DBNull.Value,
        DateTimeOffset dto => dto.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture) + "+00:00",
        DateTime dt => (dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : dt).ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
        Guid g => g.ToByteArray(),
        bool b => b ? 1L : 0L,
        Enum e => Convert.ToInt64(e, CultureInfo.InvariantCulture),
        decimal m => m,          // bound as TEXT; NUMERIC affinity stores it as a number when lossless
        _ => value,
    };
}

/// <summary>Maps <see cref="SqliteException"/> result codes to CL.Data errors.</summary>
public sealed class SqliteErrorClassifier : IErrorClassifier
{
    // Primary result codes (https://sqlite.org/rescode.html).
    private const int Busy = 5, Locked = 6, ReadOnly = 8, Interrupt = 9, Full = 13, CantOpen = 14, Constraint = 19, Auth = 23;

    // Extended constraint codes.
    private const int ConstraintCheck = 275, ConstraintForeignKey = 787, ConstraintNotNull = 1299,
        ConstraintPrimaryKey = 1555, ConstraintUnique = 2067;

    /// <inheritdoc />
    public ErrorClassification Classify(Exception exception)
    {
        if (exception is not SqliteException sqlite)
            return new(DataErrors.FromException(exception), exception is TimeoutException);

        var details = $"SQLite error {sqlite.SqliteExtendedErrorCode}: {sqlite.Message}";
        return sqlite.SqliteErrorCode switch
        {
            Busy or Locked => new(DataErrors.LockTimeout(details), true),
            Interrupt => new(DataErrors.Cancelled(details), false),
            Constraint => sqlite.SqliteExtendedErrorCode switch
            {
                ConstraintUnique or ConstraintPrimaryKey => new(DataErrors.DuplicateKey("A row with the same key already exists.", details), false),
                ConstraintForeignKey => new(DataErrors.ForeignKeyViolation("A foreign key constraint rejected the change.", details), false),
                ConstraintNotNull or ConstraintCheck => new(DataErrors.ConstraintViolation("A constraint rejected the change.", details), false),
                _ => new(DataErrors.ConstraintViolation("A constraint rejected the change.", details), false),
            },
            ReadOnly => new(DataErrors.AccessDenied("The SQLite database is read-only.", details), false),
            CantOpen => new(DataErrors.Unavailable("The SQLite database file cannot be opened.", details), false),
            Auth => new(DataErrors.AccessDenied("SQLite denied the operation.", details), false),
            Full => new(DataErrors.ProviderError("The SQLite database or disk is full.", details), false),
            _ => new(DataErrors.ProviderError("The SQLite operation failed.", details), false),
        };
    }
}
