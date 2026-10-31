using CL.Data.Admin;
using CL.Data.Configuration;
using CL.Data.Sql;
using Npgsql;

namespace CL.Data.PostgreSql;

/// <summary>PostgreSQL databases through the <c>postgres</c> maintenance database; DROP uses WITH (FORCE).</summary>
public sealed class PostgreSqlDatabaseAdmin : ServerDatabaseAdmin
{
    /// <inheritdoc />
    protected override string? ServerDatabase => "postgres";

    /// <inheritdoc />
    protected override string? DatabaseOf(string connectionString) => new NpgsqlConnectionStringBuilder(connectionString).Database;

    /// <inheritdoc />
    protected override string WithDatabase(string connectionString, string? database) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Database = database, Pooling = false }.ConnectionString;

    /// <inheritdoc />
    protected override SqlText ExistsSql(string database) => $"SELECT COUNT(*) FROM pg_database WHERE datname = {database}";

    /// <inheritdoc />
    protected override IReadOnlyList<string> CreateSql(string database, DataConnectionConfig config) => [$"CREATE DATABASE {Quote(database)}"];

    /// <inheritdoc />
    protected override IReadOnlyList<string> DropSql(string database)
    {
        NpgsqlConnection.ClearAllPools();   // our own idle connections to it
        return [$"DROP DATABASE IF EXISTS {Quote(database)} WITH (FORCE)"];
    }

    private static string Quote(string database) => "\"" + Checked(database).Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
