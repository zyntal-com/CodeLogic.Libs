using System.Data.Common;

namespace CL.Data.Execution;

/// <summary>
/// A <see cref="DbDataSource"/> that runs per-connection setup after a connection opens (SQLite PRAGMAs,
/// session variables). For providers whose driver has no data source of its own, or whose setup the driver
/// cannot express in the connection string.
/// </summary>
public abstract class InitializingDataSource(string connectionString) : DbDataSource
{
    /// <inheritdoc />
    public override string ConnectionString { get; } = connectionString;

    /// <summary>Creates an unopened driver connection.</summary>
    protected abstract DbConnection CreateDriverConnection();

    /// <summary>Runs setup on a freshly opened connection. The default does nothing.</summary>
    protected virtual ValueTask InitializeAsync(DbConnection connection, CancellationToken ct) => ValueTask.CompletedTask;

    /// <summary>Runs setup synchronously. The default blocks on <see cref="InitializeAsync"/>.</summary>
    protected virtual void Initialize(DbConnection connection) => InitializeAsync(connection, default).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    protected override DbConnection CreateDbConnection() => CreateDriverConnection();

    /// <inheritdoc />
    protected override DbConnection OpenDbConnection()
    {
        var connection = CreateDriverConnection();
        try
        {
            connection.Open();
            Initialize(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    protected override async ValueTask<DbConnection> OpenDbConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = CreateDriverConnection();
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await InitializeAsync(connection, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
