# CodeLogic.MSSQL

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.MSSQL)](https://www.nuget.org/packages/CodeLogic.MSSQL)

Typed SQL Server 2019+ and Azure SQL data access for CodeLogic 4: repositories, LINQ-shaped queries, stable paging, schema synchronization, migrations, caching, resilience, and operational diagnostics.

```bash
dotnet add package CodeLogic.MSSQL
```

```csharp
using CL.MSSQL;
using CL.MSSQL.Models;

[Table(Name = "users", Schema = "dbo")]
public sealed class User
{
    [Column(DataType = DataType.BigInt, Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(DataType = DataType.NVarChar, Size = 160, Unique = true, NotNull = true)]
    public string Email { get; set; } = "";

    [Column(DataType = DataType.DateTime2, NotNull = true)]
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

await Libraries.LoadAsync<MSSQLLibrary>();
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

var mssql = Libraries.Get<MSSQLLibrary>()!;
await mssql.SyncTableAsync<User>();
var inserted = await mssql.GetRepository<User>().InsertAsync(new User { Email = "ada@example.com" });

// Idempotent insert (re-sent batches keyed by a unique id): duplicates are a normal outcome
var outcome = await mssql.GetRepository<User>().InsertIgnoreAsync(new User { Email = "ada@example.com" });
var recent = await mssql.Query<User>()
    .Where(user => user.CreatedUtc >= DateTime.UtcNow.AddDays(-7))
    .OrderByDescending(user => user.CreatedUtc)
    .Take(20)
    .ToListAsync();
```

Configuration lives in `config.mssql.json`. Structured SQL-login and integrated-security settings are supported; an explicit `connectionString` overrides them and enables all `Microsoft.Data.SqlClient` authentication modes, including Microsoft Entra authentication. The package includes the matching `Microsoft.Data.SqlClient.Extensions.Azure` 7.x provider required by SqlClient 7.

```json
{
  "databases": {
    "Default": {
      "enabled": true,
      "host": "localhost",
      "port": 1433,
      "database": "app",
      "authenticationMode": "sqlLogin",
      "username": "app_user",
      "password": "secret",
      "encrypt": true,
      "trustServerCertificate": false,
      "defaultSchema": "dbo",
      "syncMode": "production"
    }
  }
}
```

Highlights include `OUTPUT INSERTED` identity handling, dynamically parameter-capped batches, non-`MERGE` serializable upserts, `TOP`/`OFFSET … FETCH`, SQL Server-native type inference and JSON checks, `sys.*` catalog schema management, `sp_getapplock`, catalog DDL snapshots, transient Azure retry, rolling-window N+1 detection, and cached estimated ShowPlan XML capture.

See the [full documentation](../docs/libs/mssql/index.md) and [capability parity matrix](../docs/libs/mssql/parity.md).
