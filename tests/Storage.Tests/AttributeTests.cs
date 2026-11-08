using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Providers;
using CL.Storage.Providers.Ftp;
using CL.Storage.Providers.Local;
using Xunit;

namespace Storage.Tests;

/// <summary>Covers permission formatting, FTP attribute mapping, and local attributes and links.</summary>
public sealed class AttributeTests
{
    [Theory]
    [InlineData(0x1ED, "rwxr-xr-x", "0755")]
    [InlineData(0x1A4, "rw-r--r--", "0644")]
    [InlineData(0x9ED, "rwsr-xr-x", "4755")]
    [InlineData(0x5ED, "rwxr-sr-x", "2755")]
    [InlineData(0x3FF, "rwxrwxrwt", "1777")]
    [InlineData(0x3FE, "rwxrwxrwT", "1776")]
    [InlineData(0x0, "---------", "0000")]
    public void Modes_format_like_ls(int mode, string text, string octal)
    {
        Assert.Equal(text, UnixPermissions.Format(mode));
        Assert.Equal(octal, UnixPermissions.ToOctal(mode));
        Assert.True(UnixPermissions.TryParseOctal(octal, out var parsed));
        Assert.Equal(mode, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("8")]
    [InlineData("rwx")]
    [InlineData("17777")]
    [InlineData("-1")]
    public void Invalid_octal_text_is_rejected(string text)
    {
        Assert.False(UnixPermissions.TryParseOctal(text, out _));
    }

    [Theory]
    [InlineData(755, 0x1ED)]
    [InlineData(4755, 0x9ED)]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    [InlineData(789, null)]
    public void Ftp_decimal_chmod_digits_become_permission_bits(int chmod, int? expected)
    {
        Assert.Equal(expected, FtpStorageBackend.ModeOf(chmod));
    }

    [Fact]
    public void Ftp_times_are_never_shifted_by_the_local_time_zone()
    {
        var unspecified = new DateTime(2021, 3, 4, 5, 6, 7, DateTimeKind.Unspecified);

        Assert.Equal(new DateTimeOffset(2021, 3, 4, 5, 6, 7, TimeSpan.Zero), FtpStorageBackend.Utc(unspecified));
        Assert.Null(FtpStorageBackend.Utc(DateTime.MinValue));
    }

    [Theory]
    [InlineData("/home/u/dir", "target.txt", "/home/u/dir/target.txt")]
    [InlineData("/home/u/dir", "../other/t", "/home/u/other/t")]
    [InlineData("/home/u/dir", "./a/./b", "/home/u/dir/a/b")]
    [InlineData("/", "../../x", "/x")]
    public void Relative_link_targets_resolve_against_their_directory(string directory, string relative, string expected)
    {
        Assert.Equal(expected, RemotePathResolver.Combine(directory, relative));
    }

    [Fact]
    public async Task Local_timestamps_can_be_set()
    {
        using var directory = new TestDirectory();
        var storage = Local(directory.Path);
        await storage.UploadBytesAsync("file.txt", [1]);
        var stamp = new DateTimeOffset(2021, 3, 4, 5, 6, 7, TimeSpan.Zero);

        Assert.True((await storage.SetTimestampsAsync("file.txt", stamp, stamp.AddHours(1))).IsSuccess);

        var info = (await storage.GetInfoAsync("file.txt")).Value!;
        Assert.Equal(stamp, info.LastModified);
        Assert.Equal(stamp.AddHours(1), info.LastAccessed);
        Assert.NotNull(info.Created);
    }

    [Fact]
    public async Task Local_timestamps_on_a_missing_item_are_not_found()
    {
        using var directory = new TestDirectory();

        var result = await Local(directory.Path).SetTimestampsAsync("missing.txt", DateTimeOffset.UtcNow);

        Assert.Equal(StorageErrors.NotFoundCode, result.Error?.Code);
    }

    [Fact]
    public async Task Local_dot_files_are_hidden()
    {
        using var directory = new TestDirectory();
        var storage = Local(directory.Path);
        await storage.UploadBytesAsync(".env", [1]);

        Assert.True((await storage.GetInfoAsync(".env")).Value!.IsHidden);
    }

    [Fact]
    public async Task Local_permissions_follow_the_platform()
    {
        using var directory = new TestDirectory();
        var storage = Local(directory.Path);
        await storage.UploadBytesAsync("file.txt", [1]);

        var result = await storage.SetPermissionsAsync("file.txt", 0x180);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(StorageErrors.UnsupportedCode, result.Error?.Code);
            Assert.False(storage.Capabilities.Supports(StorageFeature.Permissions));
        }
        else
        {
            Assert.True(result.IsSuccess);
            Assert.Equal(0x180, (await storage.GetInfoAsync("file.txt")).Value!.UnixMode);
        }
    }

    [Fact]
    public async Task Local_links_are_created_relative_and_read_back()
    {
        using var directory = new TestDirectory();
        var storage = Local(directory.Path, followLinks: true);
        await storage.UploadBytesAsync("data/target.txt", [7]);

        var created = await storage.CreateLinkAsync("links/link.txt", "data/target.txt");
        if (created.Error?.Code == StorageErrors.PermissionDeniedCode)
            return; // Windows without Developer Mode cannot create symbolic links.
        Assert.True(created.IsSuccess, created.Error?.ToString());

        var link = await storage.ReadLinkAsync("links/link.txt");
        Assert.True(link.IsSuccess, link.Error?.ToString());
        Assert.Equal("data/target.txt", link.Value!.StoragePath);
        Assert.False(Path.IsPathRooted(link.Value.RawTarget));
        Assert.Equal(StorageErrors.ConflictCode, (await storage.CreateLinkAsync("links/link.txt", "data/target.txt")).Error?.Code);
    }

    [Fact]
    public async Task Reading_a_regular_file_as_a_link_is_a_conflict()
    {
        using var directory = new TestDirectory();
        var storage = Local(directory.Path);
        await storage.UploadBytesAsync("file.txt", [1]);

        Assert.Equal(StorageErrors.ConflictCode, (await storage.ReadLinkAsync("file.txt")).Error?.Code);
        Assert.Equal(StorageErrors.NotFoundCode, (await storage.ReadLinkAsync("missing")).Error?.Code);
    }

    [Fact]
    public async Task Links_cannot_point_outside_the_root()
    {
        using var directory = new TestDirectory();

        var result = await Local(directory.Path).CreateLinkAsync("link", "../escape");

        Assert.Equal(StorageErrors.InvalidPathCode, result.Error?.Code);
    }

    [Fact]
    public async Task Services_without_attribute_support_report_unsupported()
    {
        var storage = new FakeStorageBackend("fake");

        Assert.Equal(StorageErrors.UnsupportedCode, (await storage.SetPermissionsAsync("a", 0x1ED)).Error?.Code);
        Assert.Equal(StorageErrors.UnsupportedCode, (await storage.SetTimestampsAsync("a", DateTimeOffset.UtcNow)).Error?.Code);
        Assert.Equal(StorageErrors.UnsupportedCode, (await storage.ReadLinkAsync("a")).Error?.Code);
        Assert.Equal(StorageErrors.InvalidContentCode, (await storage.SetPermissionsAsync("a", "999")).Error?.Code);
    }

    private static LocalStorageBackend Local(string root, bool followLinks = false) =>
        new("local", new LocalConnectionConfig { RootPath = root, FollowLinks = followLinks });
}

public sealed class NativeFolderMoveTests
{
    [Fact]
    public async Task Session_and_webdav_backends_advertise_native_folder_moves()
    {
        const StorageFeature nativeFolderMove = StorageFeature.DirectoryMove | StorageFeature.ServerSideMove | StorageFeature.AtomicMove;
        await using var ftp = new FtpStorageBackend("ftp", () => new FluentFTP.AsyncFtpClient("localhost"));
        await using var sftp = new CL.Storage.Providers.Sftp.SftpStorageBackend("sftp", () => new Renci.SshNet.SftpClient("localhost", "u", "p"));
        await using var webDav = new CL.Storage.Providers.WebDav.WebDavStorageBackend("dav", new WebDAVClient.Client(new HttpClient()));

        Assert.True(ftp.Capabilities.Supports(nativeFolderMove));
        Assert.True(sftp.Capabilities.Supports(nativeFolderMove));
        // A WebDAV collection MOVE may be done member by member (207 Multi-Status): not atomic.
        Assert.True(webDav.Capabilities.Supports(StorageFeature.DirectoryMove | StorageFeature.ServerSideMove));
        Assert.False(webDav.Capabilities.Supports(StorageFeature.AtomicMove));
    }
}
