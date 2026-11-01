using System.Reflection;
using System.Text;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Providers.Ftp;
using CL.Storage.Providers.Local;
using CL.Storage.Registry;
using CodeLogic.Core.Configuration;
using Xunit;

namespace Storage.Tests;

/// <summary>Security audit 2026-11-01: path control characters, reserved names, secret fields and lock markers.</summary>
public sealed class SecurityHardeningTests
{
    private static LocalStorageBackend Local(string root) => new("local", new LocalConnectionConfig { RootPath = root });

    [Theory]
    [InlineData("x\r\nDELE /other/file")]
    [InlineData("dir/name\n")]
    [InlineData("a\rb")]
    [InlineData("nul\0byte")]
    [InlineData("tab\tname")]
    [InlineData("bell\u0007")]
    [InlineData("unit\u001Fsep")]
    [InlineData("del\u007Fname")]
    public void Paths_with_control_characters_are_rejected(string path)
    {
        var result = StoragePath.Normalize(path);
        Assert.True(result.IsFailure);
        Assert.Equal(StorageErrors.InvalidPathCode, result.Error!.Code);
    }

    [Theory]
    [InlineData("caf\u00E9/r\u00E9sum\u00E9.txt")]
    [InlineData("space name/with spaces.txt")]
    [InlineData("\u00A0nbsp and \u0080 C1 stay")]
    public void Printable_and_non_ascii_names_still_normalize(string path) =>
        Assert.True(StoragePath.Normalize(path).IsSuccess);

    [Fact]
    public void Ftp_raw_command_arguments_are_checked_for_control_characters()
    {
        Assert.Null(FtpStorageBackend.RawArgumentError("/root/dir"));
        Assert.Equal(StorageErrors.InvalidPathCode, FtpStorageBackend.RawArgumentError("/root/x\r\nDELE /etc/passwd")!.Code);
        Assert.Equal(StorageErrors.InvalidPathCode, FtpStorageBackend.RawArgumentError("/root/\0")!.Code);
    }

    [Fact]
    public async Task An_ftp_path_with_crlf_fails_before_any_connection_is_opened()
    {
        // A client factory that throws: any attempt to connect would surface as a different error.
        await using var ftp = new FtpStorageBackend("ftp", () => throw new InvalidOperationException("must not connect"));
        IStorageService storage = ftp;
        var deleted = await storage.DeleteAsync("x\r\nDELE /other/file");
        var space = await ((IStorageSpaceService)ftp).GetSpaceAsync("a\r\nRMD /");
        Assert.Equal(StorageErrors.InvalidPathCode, deleted.Error!.Code);
        Assert.Equal(StorageErrors.InvalidPathCode, space.Error!.Code);
    }

    [Theory]
    [InlineData("CON", true)]
    [InlineData("con", true)]
    [InlineData("NUL.txt", true)]
    [InlineData("nul.tar.gz", true)]
    [InlineData("PRN.", true)]
    [InlineData("AUX ", true)]
    [InlineData("aux .log", true)]
    [InlineData("COM1", true)]
    [InlineData("com9.dat", true)]
    [InlineData("LPT0", true)]
    [InlineData("lpt\u00B9", true)]
    [InlineData("CONIN$", true)]
    [InlineData("conout$.txt", true)]
    [InlineData("CONSOLE", false)]
    [InlineData("COM10", false)]
    [InlineData("LPT", false)]
    [InlineData("xCON", false)]
    [InlineData("CON_backup", false)]
    [InlineData("nul-report.txt", false)]
    public void Windows_reserved_device_names_are_recognised(string segment, bool reserved) =>
        Assert.Equal(reserved, StoragePath.IsWindowsReservedName(segment));

    [Fact]
    public async Task Local_paths_cannot_name_a_windows_device()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TestDirectory();
        await using var local = Local(directory.Path);
        IStorageService storage = local;
        foreach (var path in new[] { "CON", "logs/NUL.txt", "com1.log", "a/LPT1/b.txt" })
        {
            var written = await storage.UploadBytesAsync(path, [1, 2, 3]);
            Assert.Equal(StorageErrors.InvalidPathCode, written.Error?.Code);
        }
        Assert.True((await storage.UploadBytesAsync("CONSOLE.txt", [1])).IsSuccess);
    }

    [Fact]
    public void Credential_fields_are_marked_secret_so_masked_exports_hide_them()
    {
        var configurationTypes = typeof(StorageConfig).Assembly.GetExportedTypes()
            .Where(type => type.Namespace == typeof(StorageConfig).Namespace && type.IsClass);
        var credentialNames = new[] { "Password", "Passphrase", "PrivateKeyContent", "Token", "Key", "Secret", "ConnectionString", "CredentialsJson" };
        var checkedFields = new List<string>();
        foreach (var type in configurationTypes)
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.PropertyType != typeof(string) || property.Name.EndsWith("Path", StringComparison.Ordinal)) continue;
                if (!credentialNames.Any(name => property.Name.EndsWith(name, StringComparison.Ordinal))) continue;
                checkedFields.Add($"{type.Name}.{property.Name}");
                Assert.True(property.GetCustomAttribute<ConfigFieldAttribute>()?.Secret == true, $"{type.Name}.{property.Name} must be [ConfigField(Secret = true)]");
            }
        }
        Assert.Contains("StorageProxyConfig.Password", checkedFields);
        Assert.Contains("SftpJumpHostConfig.Password", checkedFields);
        Assert.Contains("SftpJumpHostConfig.PrivateKeyContent", checkedFields);
        Assert.Contains("SftpJumpHostConfig.PrivateKeyPassphrase", checkedFields);
    }

    [Fact]
    public void Object_stores_do_not_claim_a_resumable_upload_nothing_implements()
    {
        foreach (var backend in new[] { "CL.Storage.Providers.Azure.AzureBlobStorageBackend", "CL.Storage.Providers.GoogleCloud.GoogleCloudStorageBackend" })
        {
            var type = typeof(StorageConfig).Assembly.GetType(backend, throwOnError: true)!;
            var capabilities = type.GetFields(BindingFlags.NonPublic | BindingFlags.Static)
                .Where(field => field.FieldType == typeof(StorageCapabilities))
                .Select(field => (StorageCapabilities)field.GetValue(null)!)
                .ToArray();
            Assert.NotEmpty(capabilities);
            Assert.All(capabilities, value => Assert.False(value.Supports(StorageFeature.ResumableUpload), backend));
        }
    }

    [Fact]
    public async Task Part_lock_markers_do_not_disclose_the_machine_name_or_process_id()
    {
        using var directory = new TestDirectory();
        await using var local = Local(directory.Path);
        var lease = (await PartLease.AcquireAsync(local, ".cl-storage-part-sec", createParents: true, CancellationToken.None)).Value!;
        try
        {
            var marker = Encoding.UTF8.GetString((await ((IStorageService)local).DownloadBytesAsync(PartLease.MarkerPath(".cl-storage-part-sec"))).Value!);
            var lines = marker.Split('\n');
            Assert.Equal("cl-storage-part-lock/2", lines[0]);
            Assert.Equal(5, lines.Length);
            Assert.DoesNotContain(Environment.MachineName, marker, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(lines, line => line == Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.True((await lease.StillOwnedAsync(CancellationToken.None)).Value);
        }
        finally
        {
            await lease.ReleaseAsync();
        }
    }

    [Fact]
    public async Task A_hashed_marker_from_a_running_process_on_this_machine_is_not_taken_over()
    {
        using var directory = new TestDirectory();
        await using var local = Local(directory.Path);
        // Another process on this machine that is running: the test host's parent cannot be faked, so use a process
        // that is certainly alive (this one) through a marker written with a different owner id and start time zero.
        var marker = PartLease.FormatMarker(Guid.NewGuid().ToString("N"), Environment.MachineName, Environment.ProcessId, 0);
        // This process holds no such part file, but a start time of zero does not match it exactly, so the marker is
        // judged by whether the process runs; it does, so the part stays held.
        await ((IStorageService)local).UploadBytesAsync(PartLease.MarkerPath(".cl-storage-part-run"), Encoding.UTF8.GetBytes(marker));
        var taken = await PartLease.AcquireAsync(local, ".cl-storage-part-run", createParents: true, CancellationToken.None);
        Assert.True(taken.IsSuccess);
        Assert.Null(taken.Value);

        // A hashed marker for a process id that does not exist is stale and is taken over.
        var dead = PartLease.FormatMarker(Guid.NewGuid().ToString("N"), Environment.MachineName, int.MaxValue - 7, 1);
        await ((IStorageService)local).UploadBytesAsync(PartLease.MarkerPath(".cl-storage-part-dead"), Encoding.UTF8.GetBytes(dead));
        var takenOver = (await PartLease.AcquireAsync(local, ".cl-storage-part-dead", createParents: true, CancellationToken.None)).Value;
        Assert.NotNull(takenOver);
        await takenOver!.ReleaseAsync();
    }
}
