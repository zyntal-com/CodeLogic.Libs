using CL.Data.Backup;
using CL.Data.Schema;

namespace Data.Tests.Schema;

public sealed class BackupValidatorTests
{
    [Theory]
    [InlineData("bigint")]
    [InlineData("varchar(64)")]
    [InlineData("decimal(18, 2)")]
    [InlineData("nvarchar(max)")]
    [InlineData("double precision")]
    [InlineData("character varying(255)")]
    [InlineData("timestamp(3) with time zone")]
    [InlineData("bigint unsigned")]
    [InlineData("text[]")]
    [InlineData("interval day to second")]
    [InlineData("enum('a','it''s')")]
    public void Accepts_store_types(string type) => Assert.True(BackupValidator.IsStoreType(type));

    [Theory]
    [InlineData("")]
    [InlineData("int); DROP TABLE t; --")]
    [InlineData("int not null")]
    [InlineData("int references users on delete cascade")]
    [InlineData("int default 1")]
    [InlineData("varchar(64) check (1=1)")]
    [InlineData(@"enum('a\', 'b')")]
    [InlineData("int -- comment")]
    [InlineData("myschema.mytype")]
    public void Rejects_store_types(string type) => Assert.False(BackupValidator.IsStoreType(type));

    [Theory]
    [InlineData("0")]
    [InlineData("-1.5")]
    [InlineData("1e3")]
    [InlineData("'abc'")]
    [InlineData("'it''s'")]
    [InlineData("N'abc'")]
    [InlineData("b'0'")]
    [InlineData("((0))")]
    [InlineData("(getdate())")]
    [InlineData("CURRENT_TIMESTAMP")]
    [InlineData("now()")]
    [InlineData("gen_random_uuid()")]
    [InlineData("'x'::character varying")]
    [InlineData("'{}'::text[]")]
    [InlineData("(CONVERT([bit],(0)))")]
    [InlineData("(datetime('now'))")]
    [InlineData("NULL")]
    public void Accepts_defaults(string sql) => Assert.True(BackupValidator.IsSafeDefault(sql));

    [Theory]
    [InlineData("0); DROP TABLE t; --")]
    [InlineData("(SELECT 1)")]
    [InlineData("1 -- x")]
    [InlineData("1union select 2")]
    [InlineData(@"'a\' ; drop table t; --'")]
    [InlineData("'unterminated")]
    [InlineData("(1")]
    [InlineData("1)")]
    [InlineData("\"col\"")]
    [InlineData("pg_sleep(10)")]
    [InlineData("1 /* x */")]
    [InlineData("'x'::text select")]
    public void Rejects_defaults(string sql) => Assert.False(BackupValidator.IsSafeDefault(sql));

    private static BackupManifest Manifest(string file, ColumnSchema? column = null, Dictionary<string, string>? options = null) =>
        new(1, "sqlite", DateTimeOffset.UnixEpoch,
        [
            new BackupTable("t", null, file, 0, new TableSchema("t", null, [column ?? new ColumnSchema("id", "integer", false)], ["id"])
            {
                Options = options ?? [],
            }),
        ]);

    [Fact]
    public void Rows_files_must_stay_in_the_backup_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "cl-backup-check");
        Assert.Null(BackupValidator.Check(Manifest("001-t.jsonl"), root));
        Assert.NotNull(BackupValidator.Check(Manifest("../001-t.jsonl"), root));
        Assert.NotNull(BackupValidator.Check(Manifest(Path.Combine("..", "cl-backup-check-other", "x.jsonl")), root));
        Assert.NotNull(BackupValidator.Check(Manifest(Path.GetFullPath(Path.Combine(root, "..", "x.jsonl"))), root));
        Assert.NotNull(BackupValidator.Check(Manifest(""), root));
    }

    [Fact]
    public void Collations_and_options_must_be_plain_names()
    {
        var root = Path.GetTempPath();
        Assert.Null(BackupValidator.Check(Manifest("f", new ColumnSchema("c", "text", true) { Collation = "utf8mb4_bin" }), root));
        Assert.NotNull(BackupValidator.Check(Manifest("f", new ColumnSchema("c", "text", true) { Collation = "x NOT NULL" }), root));
        Assert.Null(BackupValidator.Check(Manifest("f", options: new() { ["engine"] = "InnoDB" }), root));
        Assert.NotNull(BackupValidator.Check(Manifest("f", options: new() { ["engine"] = "InnoDB; DROP TABLE t" }), root));
    }
}
