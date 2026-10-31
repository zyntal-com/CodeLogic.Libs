using CL.Data.Errors;
using CL.Data.Execution;
using CL.Data.Sql;
using CodeLogic.Core.Results;

namespace CL.Data.MySql;

/// <summary>MySQL-only operations on a CL.Data database: <c>db.MySql</c>.</summary>
public static class MySqlDatabaseExtensions
{
    extension(Database db)
    {
        /// <summary>MySQL-only operations; each fails with <c>data.unsupported</c> on another provider.</summary>
        public MySqlFeatures MySql => new(db);
    }
}

/// <summary>One row of a MySQL table-maintenance result (<c>OPTIMIZE</c>, <c>ANALYZE</c>).</summary>
/// <param name="Table">Table the message is about.</param>
/// <param name="Operation">Operation (<c>optimize</c>, <c>analyze</c>).</param>
/// <param name="Type">Message type (<c>status</c>, <c>note</c>, <c>error</c>).</param>
/// <param name="Text">Message text.</param>
public sealed record MySqlTableMessage(string Table, string Operation, string Type, string Text);

/// <summary>MySQL-only operations (<see cref="MySqlDatabaseExtensions"/>).</summary>
public readonly struct MySqlFeatures(Database db)
{
    /// <summary>Rebuilds <typeparamref name="T"/>'s table and reclaims unused space (<c>OPTIMIZE TABLE</c>).</summary>
    public ValueTask<Result<IReadOnlyList<MySqlTableMessage>>> OptimizeTableAsync<T>(CancellationToken ct = default) => MaintainAsync<T>("OPTIMIZE", ct);

    /// <summary>Refreshes the key distribution statistics of <typeparamref name="T"/>'s table (<c>ANALYZE TABLE</c>).</summary>
    public ValueTask<Result<IReadOnlyList<MySqlTableMessage>>> AnalyzeTableAsync<T>(CancellationToken ct = default) => MaintainAsync<T>("ANALYZE", ct);

    private async ValueTask<Result<IReadOnlyList<MySqlTableMessage>>> MaintainAsync<T>(string statement, CancellationToken ct)
    {
        if (db.Provider is not MySqlDataProvider) return DataErrors.Unsupported($"{statement} TABLE needs MySQL; this connection uses {db.Provider.Name}.");
        var table = db.Admin.QuotedTable<T>();
        if (table.IsFailure) return table.Error!;
        // A read (not ExecuteSql): maintenance changes no rows, so cached results stay valid.
        var rows = await db.SqlReadAsync(SqlText.Raw($"{statement} TABLE {table.Value}"),
            static r => new MySqlTableMessage(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)), ct).ConfigureAwait(false);
        if (rows.IsFailure) return rows.Error!;
        return rows.Value!.FirstOrDefault(m => m.Type == "error") is { } failed
            ? DataErrors.ProviderError($"{statement} TABLE failed.", failed.Text)
            : rows.Value!;
    }
}
