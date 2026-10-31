using CL.Data.Admin;
using CL.Data.Configuration;
using CL.Data.Sql;
using MySqlConnector;

namespace CL.Data.MySql;

/// <summary>MySQL databases (schemas): information_schema.SCHEMATA, CREATE/DROP DATABASE with the connection's charset.</summary>
public sealed class MySqlDatabaseAdmin : ServerDatabaseAdmin
{
    /// <inheritdoc />
    protected override string? ServerDatabase => null;

    /// <inheritdoc />
    protected override string? DatabaseOf(string connectionString) => new MySqlConnectionStringBuilder(connectionString).Database;

    /// <inheritdoc />
    protected override string WithDatabase(string connectionString, string? database) =>
        new MySqlConnectionStringBuilder(connectionString) { Database = database ?? "", Pooling = false }.ConnectionString;

    /// <inheritdoc />
    protected override SqlText ExistsSql(string database) => $"SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = {database}";

    /// <inheritdoc />
    protected override IReadOnlyList<string> CreateSql(string database, DataConnectionConfig config)
    {
        var options = config.MySql ?? new MySqlOptions();
        return [$"CREATE DATABASE IF NOT EXISTS {Quote(database)} CHARACTER SET {options.CharacterSet} COLLATE {options.Collation}"];
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> DropSql(string database) => [$"DROP DATABASE IF EXISTS {Quote(database)}"];

    private static string Quote(string database) => "`" + Checked(database).Replace("`", "``", StringComparison.Ordinal) + "`";
}
