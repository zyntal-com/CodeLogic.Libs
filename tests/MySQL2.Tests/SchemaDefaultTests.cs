using CL.MySQL2;
using CL.MySQL2.Models;
using Xunit;

namespace MySQL2.Tests;

// Empty-string defaults: `DefaultValue = ""` means DEFAULT '' on CHAR/VARCHAR, and sync tells a
// NOT NULL column without a default apart from one whose default is ''.

[Table(Name = "it_defaults")]
public sealed class DefaultsRow
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }

    // Model: empty default; the table starts without one.
    [Column(Name = "code", DataType = DataType.VarChar, Size = 16, NotNull = true, DefaultValue = "")] public string Code { get; set; } = "";

    // Model: quoted literal, the form that already worked.
    [Column(Name = "tag", DataType = DataType.VarChar, Size = 16, NotNull = true, DefaultValue = "''")] public string Tag { get; set; } = "";

    // Model: no default; the table starts with DEFAULT ''.
    [Column(Name = "note", DataType = DataType.VarChar, Size = 16, NotNull = true)] public string Note { get; set; } = "";

    // TEXT allows no literal default: "" is ignored.
    [Column(Name = "body", DataType = DataType.Text, NotNull = true, DefaultValue = "")] public string Body { get; set; } = "";
}

[Collection("codelogic")]
public sealed class SchemaDefaultTests
{
    private readonly MySQL2RuntimeFixture _fx;
    private MySQL2Library Mysql => _fx.Mysql;

    public SchemaDefaultTests(MySQL2RuntimeFixture fx) => _fx = fx;

    private Task<Dictionary<string, string?>> DefaultsAsync() =>
        Mysql.ConnectionManager.ExecuteWithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COLUMN_NAME, COLUMN_DEFAULT FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='it_defaults'";
            var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                map[r.GetString(0)] = r.IsDBNull(1) ? null : r.GetString(1).Trim('\'');
            return map;
        }, "Default");

    private async Task ForgetSyncStateAsync()
    {
        try { await _fx.Exec("DELETE FROM `__schema_state` WHERE `TableName` = 'it_defaults'"); }
        catch (Exception) { /* no state table yet */ }
    }

    [DbFact]
    public async Task Sync_adds_and_drops_empty_string_defaults_and_then_settles()
    {
        await _fx.Exec("DROP TABLE IF EXISTS it_defaults");
        await _fx.Exec("""
            CREATE TABLE it_defaults (
              id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
              code VARCHAR(16) NOT NULL,
              tag VARCHAR(16) NOT NULL,
              note VARCHAR(16) NOT NULL DEFAULT '',
              body TEXT NOT NULL
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
            """);
        await ForgetSyncStateAsync();

        var first = await Mysql.SyncTableAsync<DefaultsRow>(createBackup: false);
        Assert.True(first.IsSuccess, first.Error?.Message);

        var defaults = await DefaultsAsync();
        Assert.Equal("", defaults["code"]);
        Assert.Equal("", defaults["tag"]);
        Assert.Null(defaults["note"]);
        Assert.Null(defaults["body"]);

        // A second full comparison finds nothing to change (no ALTER on every boot).
        await ForgetSyncStateAsync();
        var second = await Mysql.SyncTableAsync<DefaultsRow>(createBackup: false);
        Assert.True(second.IsSuccess, second.Error?.Message);
        Assert.DoesNotContain(second.Value!.Operations, op => op.Contains("MODIFY", StringComparison.OrdinalIgnoreCase));
    }

    [DbFact]
    public async Task Rows_inserted_without_the_column_get_the_empty_default()
    {
        await _fx.Exec("DROP TABLE IF EXISTS it_defaults");
        await ForgetSyncStateAsync();
        Assert.True((await Mysql.SyncTableAsync<DefaultsRow>(createBackup: false)).IsSuccess);

        // What an older app version (that does not know `code`) would do.
        await _fx.Exec("INSERT INTO it_defaults (tag, note, body) VALUES ('t', 'n', 'b')");
        var defaults = await DefaultsAsync();
        Assert.Equal("", defaults["code"]);
    }
}
