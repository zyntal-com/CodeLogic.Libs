using CL.Data.Errors;
using CL.Data.Execution;
using CL.Data.Sql;
using CodeLogic.Core.Results;

namespace CL.Data.SqlServer;

/// <summary>SQL Server-only operations on a CL.Data database: <c>db.SqlServer</c>.</summary>
public static class SqlServerDatabaseExtensions
{
    extension(Database db)
    {
        /// <summary>SQL Server-only operations; each fails with <c>data.unsupported</c> on another provider.</summary>
        public SqlServerFeatures SqlServer => new(db);
    }
}

/// <summary>SQL Server-only operations (<see cref="SqlServerDatabaseExtensions"/>).</summary>
public readonly struct SqlServerFeatures(Database db)
{
    /// <summary>Refreshes the query optimizer statistics of <typeparamref name="T"/>'s table (<c>UPDATE STATISTICS</c>, optionally <c>WITH FULLSCAN</c>).</summary>
    public async ValueTask<Result> UpdateStatisticsAsync<T>(bool fullScan = false, CancellationToken ct = default)
    {
        if (db.Provider is not SqlServerDataProvider)
            return DataErrors.Unsupported($"UPDATE STATISTICS needs SQL Server; this connection uses {db.Provider.Name}.");
        var table = db.Admin.QuotedTable<T>();
        if (table.IsFailure) return table.Error!;
        // A read (not ExecuteSql): statistics change no rows, so cached results stay valid.
        var done = await db.SqlReadAsync(SqlText.Raw($"UPDATE STATISTICS {table.Value}{(fullScan ? " WITH FULLSCAN" : "")}"), static _ => 0, ct)
            .ConfigureAwait(false);
        return done.IsFailure ? done.Error! : Result.Success();
    }
}
