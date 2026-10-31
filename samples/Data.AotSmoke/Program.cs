// CL.Data Native AOT smoke app: CRUD on SQLite through generated accessors, LINQ translation (interpreted
// expression trees under AOT), the plan cache, transactions, schema sync and raw SQL. Exits non-zero on any
// mismatch. Publish with `dotnet publish -c Release` (PublishAot is on); the publish must have no trim/AOT
// warnings from CL.Data or its providers.
using CL.Data;
using CL.Data.Configuration;
using CL.Data.Execution;
using CL.Data.Mapping;
using CL.Data.Sqlite;
using CodeLogic.Core.Results;

var file = Path.Combine(Path.GetTempPath(), $"cl-data-aot-{Guid.NewGuid():N}.db");
var failures = new List<string>();
void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
    if (!ok) failures.Add(what);
}
T Ok<T>(Result<T> result) => result.IsSuccess ? result.Value! : throw new InvalidOperationException(result.Error!.ToString());

try
{
    Check(!EntityAccessorRegistry.IsReflectionSupported, "reflection mapping is off (trimmed away)");
    Check(EntityAccessorRegistry.HasGenerated<Note>(), "Note has a generated accessor");

    await using var db = Ok(Database.Create("aot", SqliteDataProvider.Instance, new DataConnectionConfig { Provider = "sqlite", Database = file }));
    Ok(await db.Schema.SyncAsync<Note>());

    var notes = db.Repository<Note>();
    var first = Ok(await notes.InsertAsync(new Note { Title = "first", Priority = Priority.High, Due = DateTimeOffset.UtcNow.AddDays(1) }));
    Ok(await notes.InsertManyAsync([new Note { Title = "second", Priority = Priority.Low }, new Note { Title = "third", Priority = Priority.High, Done = true }]));
    Check(first.Id > 0, "identity written back");

    for (var round = 0; round < 3; round++)   // repeated shapes exercise the plan cache
    {
        var wanted = round == 0 ? Priority.High : Priority.Low;
        var count = Ok(await db.Query<Note>().Where(n => n.Priority == wanted && !n.Done).CountAsync());
        Check(count == 1, $"filtered count round {round}");
    }

    var titles = Ok(await db.Query<Note>().Where(n => n.Title.StartsWith("s") || n.Id == first.Id).OrderBy(n => n.Title).ToListAsync()).Select(n => n.Title).ToArray();
    Check(titles is ["first", "second"], "LIKE and OR translate");

    first.Done = true;
    Ok(await notes.UpdateAsync(first));
    Check(Ok(await db.Query<Note>().FirstOrDefaultAsync(n => n.Id == first.Id))!.Done, "update round-trips");

    Ok(await db.InTransactionAsync(async (tx, ct) =>
    {
        await tx.Repository<Note>().InsertAsync(new Note { Title = "in tx" }, ct);
        return Result<int>.Success(0);
    }));
    Check(Ok(await db.Query<Note>().CountAsync()) == 4, "transaction committed");

    var raw = Ok(await db.SqlScalarAsync<long>($"SELECT COUNT(*) FROM notes WHERE done = {true}"));
    Check(raw == 2, "raw SQL with a parameter");

    Ok(await notes.DeleteAsync(first));
    Check(Ok(await db.Query<Note>().CountAsync()) == 3, "delete");
}
catch (Exception ex)
{
    failures.Add(ex.ToString());
    Console.WriteLine(ex);
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    foreach (var f in new[] { file, file + "-wal", file + "-shm" })
        if (File.Exists(f)) File.Delete(f);
}

Console.WriteLine(failures.Count == 0 ? "CL.Data AOT smoke: OK" : $"CL.Data AOT smoke: {failures.Count} failure(s)");
return failures.Count == 0 ? 0 : 1;

/// <summary>Priority of a note.</summary>
public enum Priority { Low, High }

/// <summary>The smoke-test entity.</summary>
[Table(Name = "notes")]
public sealed class Note
{
    /// <summary>Identity.</summary>
    [Column(Primary = true, AutoIncrement = true)] public long Id { get; set; }
    /// <summary>Title.</summary>
    [Column(Size = 100)] public string Title { get; set; } = "";
    /// <summary>Priority.</summary>
    public Priority Priority { get; set; }
    /// <summary>Done flag.</summary>
    [Column(Name = "done")] public bool Done { get; set; }
    /// <summary>Optional due date.</summary>
    public DateTimeOffset? Due { get; set; }
}
