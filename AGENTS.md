# CodeLogic.Libs: guidance for agents and contributors

Independent NuGet libraries built on the [CodeLogic](https://github.com/zyntal-com/CodeLogic) framework,
published as `CodeLogic.<Name>` (source folders `CL.<Name>/`).
- **5.x (`main`):** Common, Data (with the providers Data.MySql, Data.PostgreSql, Data.SqlServer and
  Data.Sqlite), GameNetQuery, GitHelper, Imaging, Mail, NetUtils, SocialConnect, Storage, SystemStats and
  TwoFactorAuth.
- **4.x (`support/4.x`):** the same, minus Data and Imaging, plus the database libraries MSSQL, MySQL2,
  PostgreSQL and SQLite. In 5.x, CL.Data replaces those four; CL.Imaging takes over CL.Common's imaging.
Each library has its own `README.md` and `CHANGELOG.md`; shared build logic is in `Directory.Build.props`
and `Directory.Build.targets`.

## Release lines and branches

There is one release line per major version, matching the CodeLogic framework's line. Know which line
you are working on before changing anything.

| Line | Target | Integration branch | Preview channel | Stable channel | `version.txt` | CodeLogic dependency |
|------|--------|--------------------|-----------------|----------------|---------------|----------------------|
| 5.x | .NET 11, C# 15 | `main` | `prerelease/5.x` | `release/5.x` | `5.0` | `[5.0.0-preview, 6.0.0)` (`[5.0.0, 6.0.0)` once 5.0 is stable) |
| 4.x | .NET 10, C# 13 | `support/4.x` | `prerelease/4.x` | `release/4.x` | `4.9` | `[4.9.0, 5.0.0)` |

> **Current state (2026-11-01):** `main` is the 5.x line (net11, CodeLogic 5 `Result`/`Error`). CL.Data, the
> redesigned database framework, is on `main` (PR #37). The 5.x libraries are published as previews
> (`CodeLogic.Data`, `CodeLogic.Common`, `CodeLogic.Storage` and the rest at `5.0.115-preview` and later) and
> build against CodeLogic `5.0.33-preview` (`CodeLogicPackageVersion` in `Directory.Build.props`). Published
> nuspecs depend on CodeLogic `[5.0.0-preview, 6.0.0)` until CodeLogic 5.0 is stable. The `Deploy Documentation`
> workflow is **disabled**, so the public docs site keeps showing 4.9 until the 5.x libraries are released
> as stable. Do not run Promote or re-enable that workflow without the owner's explicit go-ahead.

- **Integration branches** (`main`, `support/**`) take changes through pull requests only, and the
  required checks must pass. That means the full suite, including the MySQL, PostgreSQL and SQL Server
  container jobs.
- **Channel branches** (`release/**`, `prerelease/**`) are protected by a repository ruleset. Only the
  **Promote** workflow can write to them: it pushes with the `PROMOTE_DEPLOY_KEY` deploy key, which is
  the ruleset's only bypass. Never try to push there, and never ask anyone to switch the ruleset off.
- **Shipping a change:**
  1. Open a PR into the line's integration branch.
  2. Once it's merged, run **Promote** (`source` = `main` or `support/4.x`, `channel` = `prerelease`
     or `release`).
  3. `release.yml` re-runs the suite and packs **only the libraries changed since that line's last
     tag** on that channel (`libs-v<major.minor>-<run>` for stable, e.g. `libs-v4.9-107`;
     `libs-preview-v<major.minor>-<run>` for previews).
  4. It rewrites each nuspec's CodeLogic range, pre-flights every package, then publishes.
- **Fixes for both lines:** make the fix on one integration branch, then cherry-pick it in a second
  PR into the other.
- **Order with the framework:** a library change that needs a newer CodeLogic ships after that
  CodeLogic version is on nuget.org.
- **Full description:** [`docs/versioning.md`](docs/versioning.md).

**Publishing to nuget.org is irreversible, and a partial push can't be undone.** Never merge a PR or
run Promote unless the repository owner explicitly asked for that specific merge or release.

## Building and testing

Where CodeLogic comes from:
- **Local development (temporary, while 5.x is in progress):** when the sibling clone `../CodeLogic`
  exists, libraries build against its source (`CodeLogicFromNuGet=false` by default), so CodeLogic changes
  need no publish. Keep the sibling on its `main` (5.x).
- **CI and release** pass `-p:CodeLogicFromNuGet=true` and use the published package
  (`CodeLogicPackageVersion` in `Directory.Build.props`). Before the 5.0 release the local default goes back
  to NuGet, and the CodeLogic version these libraries need must be on nuget.org first.

```sh
dotnet build CL.Storage/CL.Storage.csproj -c Release                                # 0 warnings expected
dotnet test tests/Storage.Tests -c Release
dotnet test tests/Storage.Tests -c Release -p:CodeLogicFromNuGet=true               # as CI does
```
- **`tests/Directory.Build.props` doesn't inherit the root props on purpose.** Keep its
  `TargetFramework`/`LangVersion` in step with the root.
- **Live tests skip themselves** unless their environment variables are set. Run them before a PR
  touches a provider. Use separate containers on free ports, and leave unrelated containers alone.
  - **CL.Data (5.x):** a connection string per provider, with the database created first:
    `CL_DATA_MYSQL_CS` (MySQL 8.4), `CL_DATA_PG_CS` (PostgreSQL 18), `CL_DATA_MSSQL_CS` (SQL Server 2019).
    SQLite always runs. Every provider runs the shared suites in `tests/Data.Conformance`.
    Live replica pairs: `docker compose -f tests/replicas/docker-compose.yml up -d`, then `CL_DATA_REPLICAS=1`.
    `CL_DATA_FORCE_REFLECTION=1` runs the suites without generated mapping; run both modes before a release.
    Performance budgets: `dotnet run -c Release --project benchmarks/Data.Benchmarks -- --check-budgets benchmarks/budgets.json`.
  - **4.x database libraries:** `CL_MYSQL2_TEST_HOST/PORT/DB/USER/PASS`, `CL_PG_TEST_HOST/PORT/DB/USER/PASS`,
    `CL_MSSQL_TEST_CONNECTION_STRING` (database `cl_test` created first).
  - **Storage:** `docker compose -f tests/Storage.Integration.Tests/docker-compose.yml up -d --build`,
    then the `CL_STORAGE_TEST_*` variables listed in `.github/workflows/ci-tests.yml` (job
    `storage-integration`).
  - **Mail:** `CL_MAIL_TEST_SMTP_*`; `CL_MAIL_TEST_SMTP_SECURITY=None` for a local Mailpit.
  - **Git:** `CL_GIT_TEST_REMOTE` (any public repository URL).
- **CI** (`ci-tests.yml`) is the reference for the exact images, ports and variables.

## Strong-name signing

Every assembly in CodeLogic and CodeLogic.Libs is strong-named with one key pair, public key token
`53a89305eedc5d98` (2048-bit RSA). This is assembly identity, not security.
- **Shared build files:** `build/StrongName.props` and `build/StrongName.targets` are imported by the repo build files. Keep both repos' copies identical.
- **Ordinary builds:** every local and CI build public-signs with the committed `build/codelogic.public.snk`, so it gets the final identity without the private key.
- **Releases:** they're fully signed. `release.yml` writes the private key from the repository secret `CL_STRONG_NAME_KEY` (base64 of the `.snk`) to the runner's temp directory and passes `-p:CLStrongNameKeyFile=...`. Without the secret, the release fails.
- **Verification:** `build/verify-strong-name.ps1` checks the token and a real signature in every packed assembly before anything is pushed.
- **The private key:** it is never committed (`.gitignore` blocks every `*.snk` except the public key). The owner keeps the only copy offline. Losing it would mean a new identity, which is a breaking change for anyone binding to these assemblies.
- **Friend assemblies:** test assemblies are signed too. Declare friends as `<InternalsVisibleTo Include="..." />` items in the project file, not as attributes; `StrongName.targets` adds the public key to them.
- **Opting out:** a project that can't be signed sets `<CLStrongName>false</CLStrongName>`.

## Conventions (5.x)

- **Results.** Return CodeLogic `Result<T>` / `Result` (C# 15 unions) for expected failures; throw only
  for bugs. In tests and startup code, `ValueOrThrow()` / `ThrowIfFailure()` turn a failure into a
  `ResultFailedException` that carries the `Error`. Never use property patterns on the unions
  (`is { IsSuccess: true }`); check `IsFailure`. Library-specific result wrapper types are gone; extra data belongs in the success payload
  record (e.g. `MailSendReceipt`, `ServerQuery`).
- **Errors.** `Error` is a `closed record`, so you can't subclass it. Pick the matching kind
  (`Error.NotFound/Validation/Unauthorized/Conflict/Timeout/Unavailable/Internal`) and give it a stable
  `area.snake_case` code.
  - Keep the codes in one place per library: `MailErrorCodes`, `GitErrors`, `StorageErrors`.
  - Cancellation maps to an unavailable error, following CL.Storage's convention.
- **Exhaustiveness.** CS8509 (non-exhaustive switch) is an **error** in every library. Closed types:
  `Error`, `FrameworkEvent`, `StorageConnectionConfigBase`. Adding a case breaks every switch over it,
  on purpose.
- **Query translators** (CL.Data in 5.x; MSSQL, MySQL2, PostgreSQL, SQLite in 4.x):
  - Under C# 14+, `array.Contains(x)` binds to `MemoryExtensions.Contains(ReadOnlySpan<T>, T)`, or to the
    three-argument `Contains(span, value, comparer: null)` for element types such as `int?`. Keep
    `UnwrapSpanConversion` and handle both overloads so they still become `IN (...)`.
  - CL.Data never evaluates on the client: an untranslatable expression is a `data.translation_failed`
    error.
  - Every captured value is a parameter. Never splice values into SQL text.
- **.NET 11 behavior changes to remember:**
  - A double → decimal cast is exact, so the SQLite provider (CL.SQLite in 4.x) converts through the shortest
    round-trip string.
  - Zstandard is built in: `CompressionHelper.CompressZstandard`.
- **Modern C#:** collection expressions, including `[with(StringComparer.OrdinalIgnoreCase), …]`;
  `System.Threading.Lock`; interpolated strings for logging (disabled levels cost nothing).
- **Changelog and docs:**
  - Every user-visible change gets a line in that library's `CHANGELOG.md`, under the line's section.
    The NuGet release notes come from it, capped below nuget.org's 35 000 characters.
  - Keep the library `README.md` and `docs/libs/` in step with the code.

## 4.x line (`support/4.x`)

The 4.x line takes **fixes only**: no features and no API changes. Libraries target `net10.0` with C# 13;
tests compile as C# 14, the .NET 10 default, so they exercise what consumers compile. Don't bring 5.x-only
constructs into it. 4.x consumers include CL.WebLogic2 and FragHunt.Web.
