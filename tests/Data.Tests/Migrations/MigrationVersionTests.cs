using CL.Data.Migrations;
using CodeLogic.Core.Results;

namespace Data.Tests.Migrations;

public sealed class MigrationVersionTests
{
    [Fact]
    public void Orders_semantically_then_by_order()
    {
        var versions = new[] { new MigrationVersion("1.10.0", 1), new MigrationVersion("1.9.0", 2), new MigrationVersion("1.9.0", 1), new MigrationVersion("2.0.0-beta", 1) };
        Assert.Equal(["1.9.0/001", "1.9.0/002", "1.10.0/001", "2.0.0-beta/001"], versions.Order().Select(v => v.ToString()));
        Assert.True(new MigrationVersion("1.2", 1).IsAtOrBelow("1.2.0"));
        Assert.False(new MigrationVersion("1.2.1", 1).IsAtOrBelow("1.2.0"));
        Assert.Equal(new MigrationVersion("1.2.0", 3), MigrationVersion.Parse("1.2.0/003"));
        Assert.Throws<FormatException>(() => MigrationVersion.Parse("1.2.0"));
    }

    [Fact]
    public void Duplicate_versions_are_rejected()
    {
        var a = new FakeMigration("1.0.0", 1);
        var b = new FakeMigration("1.0.0", 1);
        var db = CL.Data.Execution.Database.Create("dup", CL.Data.Sqlite.SqliteDataProvider.Instance,
            new CL.Data.Configuration.DataConnectionConfig { Provider = "sqlite", Database = ":memory:" }).ValueOrThrow();
        Assert.Throws<ArgumentException>(() => db.Migrations([a, b]));
    }

    private sealed class FakeMigration(string app, int order) : Migration(app, order, "fake")
    {
        public override ValueTask<CodeLogic.Core.Results.Result> UpAsync(IMigrationContext context, CancellationToken ct) =>
            ValueTask.FromResult(CodeLogic.Core.Results.Result.Success());
    }
}
