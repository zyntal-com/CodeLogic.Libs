using CL.Data.Admin;
using CL.Data.Configuration;
using CL.Data.Sql;
using Microsoft.Data.SqlClient;

namespace CL.Data.SqlServer;

/// <summary>SQL Server databases through <c>master</c>; DROP switches to single-user first to close other sessions.</summary>
public sealed class SqlServerDatabaseAdmin : ServerDatabaseAdmin
{
    /// <inheritdoc />
    protected override string? ServerDatabase => "master";

    /// <inheritdoc />
    protected override string? DatabaseOf(string connectionString) => new SqlConnectionStringBuilder(connectionString).InitialCatalog;

    /// <inheritdoc />
    protected override string WithDatabase(string connectionString, string? database) =>
        new SqlConnectionStringBuilder(connectionString) { InitialCatalog = database ?? "", Pooling = false }.ConnectionString;

    /// <inheritdoc />
    protected override SqlText ExistsSql(string database) => $"SELECT COUNT(*) FROM sys.databases WHERE name = {database}";

    /// <inheritdoc />
    protected override IReadOnlyList<string> CreateSql(string database, DataConnectionConfig config) => [$"CREATE DATABASE {Quote(database)}"];

    /// <inheritdoc />
    protected override IReadOnlyList<string> DropSql(string database)
    {
        SqlConnection.ClearAllPools();
        var literal = "N'" + Checked(database).Replace("'", "''", StringComparison.Ordinal) + "'";
        return [$"IF DB_ID({literal}) IS NOT NULL BEGIN ALTER DATABASE {Quote(database)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {Quote(database)}; END"];
    }

    private static string Quote(string database) => "[" + Checked(database).Replace("]", "]]", StringComparison.Ordinal) + "]";
}
