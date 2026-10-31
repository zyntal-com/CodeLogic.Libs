using CL.Data.Admin;
using CL.Data.Configuration;
using CL.Data.Errors;
using CL.Data.Providers;
using CodeLogic.Core.Results;
using Microsoft.Data.Sqlite;

namespace CL.Data.Sqlite;

/// <summary>SQLite databases are files: exists checks the file, create opens it, drop deletes it with its WAL files.</summary>
public sealed class SqliteDatabaseAdmin : DatabaseAdmin
{
    /// <inheritdoc />
    public override Result<string> ConfiguredDatabase(DataProvider provider, DataConnectionConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.Database)) return config.Database;
        var source = new SqliteConnectionStringBuilder(config.ConnectionString ?? "").DataSource;
        return string.IsNullOrWhiteSpace(source) ? DataErrors.Validation("The connection does not name a database file.") : source;
    }

    /// <inheritdoc />
    public override ValueTask<Result<bool>> ExistsAsync(DataProvider provider, DataConnectionConfig config, string database, CancellationToken ct) =>
        ValueTask.FromResult<Result<bool>>(IsMemory(database) || File.Exists(database));

    /// <inheritdoc />
    public override async ValueTask<Result> CreateAsync(DataProvider provider, DataConnectionConfig config, string database, CancellationToken ct)
    {
        if (IsMemory(database) || File.Exists(database)) return Result.Success();
        try
        {
            if (Path.GetDirectoryName(Path.GetFullPath(database)) is { Length: > 0 } directory) Directory.CreateDirectory(directory);
            var cs = new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ConnectionString;
            await using var connection = new SqliteConnection(cs);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            return DataErrors.Validation($"Could not create the SQLite database '{database}'.", ex.Message);
        }
    }

    /// <inheritdoc />
    public override ValueTask<Result> DropAsync(DataProvider provider, DataConnectionConfig config, string database, CancellationToken ct)
    {
        if (IsMemory(database)) return ValueTask.FromResult(Result.Success());
        try
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in new[] { database, database + "-wal", database + "-shm", database + "-journal" })
                if (File.Exists(file)) File.Delete(file);
            return ValueTask.FromResult(Result.Success());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult<Result>(DataErrors.Validation($"Could not delete the SQLite database '{database}'.", ex.Message));
        }
    }

    private static bool IsMemory(string database) => database.Equals(":memory:", StringComparison.OrdinalIgnoreCase);
}
