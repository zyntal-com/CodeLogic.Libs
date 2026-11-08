using System.Text;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Providers.Local;
using Xunit;

namespace Storage.Tests;

/// <summary>Covers appending, resuming interrupted uploads and downloads, and staging cleanup.</summary>
public sealed class ResumeTests
{
    private static readonly byte[] Full = Encoding.UTF8.GetBytes("0123456789abcdefghij");

    [Fact]
    public async Task Append_adds_to_the_end_and_creates_missing_files()
    {
        using var directory = new TestDirectory();
        var storage = Local(directory.Path);

        await storage.AppendAsync("log/app.log", new MemoryStream("one\n"u8.ToArray()));
        var appended = await storage.AppendAsync("log/app.log", new MemoryStream("two\n"u8.ToArray()));

        Assert.True(appended.IsSuccess, appended.Error?.ToString());
        Assert.Equal("one\ntwo\n", Encoding.UTF8.GetString((await storage.DownloadBytesAsync("log/app.log")).Value!));
        Assert.Equal(8, appended.Value!.Size);
    }

    [Fact]
    public async Task Resume_uploads_only_the_missing_tail()
    {
        using var directory = new TestDirectory();
        var storage = Local(directory.Path);
        // An earlier upload of the same source dropped after 7 bytes; its part file keeps them.
        var dropped = await storage.UploadAsync("big.bin", new DroppingStream(Full, 7), Resume());
        Assert.False(dropped.IsSuccess);
        var source = new CountingStream(Full);

        var resumed = await storage.UploadAsync("big.bin", source, Resume());

        Assert.True(resumed.IsSuccess, resumed.Error?.ToString());
        Assert.Equal(Full, (await storage.DownloadBytesAsync("big.bin")).Value);
        // Only the tail was read; a full re-upload would read all 20 bytes.
        Assert.Equal(Full.Length - 7, source.BytesRead);
    }

    /// <summary>A seekable source that delivers a given number of bytes and then fails, as a dropped upload would.</summary>
    private sealed class DroppingStream(byte[] content, int dropAt) : MemoryStream(content)
    {
        // MemoryStream routes a derived stream's span and async reads here.
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Position >= dropAt) throw new IOException("The connection was reset.");
            return base.Read(buffer, offset, (int)Math.Min(count, dropAt - Position));
        }
    }

    [Fact]
    public async Task Resume_of_a_complete_file_changes_nothing_and_a_larger_file_is_replaced()
    {
        using var directory = new TestDirectory();
        var storage = Local(directory.Path);
        await storage.UploadBytesAsync("done.bin", Full);
        await storage.SetTimestampsAsync("done.bin", new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await storage.UploadBytesAsync("stale.bin", Encoding.UTF8.GetBytes(new string('x', 40)));

        Assert.True((await storage.UploadAsync("done.bin", new MemoryStream(Full), Resume())).IsSuccess);
        Assert.Equal(2020, (await storage.GetInfoAsync("done.bin")).Value!.LastModified!.Value.Year);

        Assert.True((await storage.UploadAsync("stale.bin", new MemoryStream(Full), Resume())).IsSuccess);
        Assert.Equal(Full, (await storage.DownloadBytesAsync("stale.bin")).Value);
    }

    [Fact]
    public async Task Resume_without_an_existing_file_uploads_normally_and_needs_a_seekable_source_otherwise()
    {
        using var directory = new TestDirectory();
        var storage = Local(directory.Path);

        Assert.True((await storage.UploadAsync("new.bin", new MemoryStream(Full), Resume())).IsSuccess);
        Assert.Equal(Full, (await storage.DownloadBytesAsync("new.bin")).Value);

        await storage.UploadBytesAsync("partial.bin", Full[..3]);
        var nonSeekable = await storage.UploadAsync("partial.bin", new NonSeekable(Full), Resume());
        Assert.Equal(StorageErrors.UnsupportedCode, nonSeekable.Error?.Code);
    }

    [Fact]
    public async Task Download_to_file_resumes_a_partial_local_copy()
    {
        using var directory = new TestDirectory();
        var storage = Local(directory.CreateDirectory("remote"));
        await storage.UploadBytesAsync("f.bin", Full);
        var target = Path.Combine(directory.CreateDirectory("local"), "f.bin");
        await File.WriteAllBytesAsync(target, Full[..5]);

        var result = await storage.DownloadToFileAsync("f.bin", target, conflictPolicy: StorageConflictPolicy.Resume);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(Full, await File.ReadAllBytesAsync(target));
    }

    [Fact]
    public async Task Stale_staging_items_are_cleaned_up_but_fresh_ones_and_user_files_stay()
    {
        using var directory = new TestDirectory();
        var storage = Local(directory.Path);
        await storage.UploadBytesAsync("sub/.cl-storage-upload-old.tmp", [1]);
        await storage.SetTimestampsAsync("sub/.cl-storage-upload-old.tmp", DateTimeOffset.UtcNow.AddDays(-2));
        await storage.UploadBytesAsync("sub/.cl-storage-upload-new.tmp", [1]);
        await storage.UploadBytesAsync("sub/user.txt", [1]);
        await storage.SetTimestampsAsync("sub/user.txt", DateTimeOffset.UtcNow.AddDays(-2));

        var cleaned = await storage.CleanupStaleStagingAsync("", TimeSpan.FromDays(1));

        Assert.Equal(1, cleaned.Value);
        Assert.False((await storage.ExistsAsync("sub/.cl-storage-upload-old.tmp")).Value);
        Assert.True((await storage.ExistsAsync("sub/.cl-storage-upload-new.tmp")).Value);
        Assert.True((await storage.ExistsAsync("sub/user.txt")).Value);
    }

    private static StorageUploadOptions Resume() => new() { ConflictPolicy = StorageConflictPolicy.Resume, SourceIdentity = "test-source", SourceIdentityIsContentVersion = true };

    private static LocalStorageBackend Local(string root) => new("local", new LocalConnectionConfig { RootPath = root });

    private sealed class NonSeekable(byte[] content) : MemoryStream(content)
    {
        public override bool CanSeek => false;
    }
}

public sealed class SpaceTests
{
    [Fact]
    public async Task Local_space_reports_the_volume_holding_the_root()
    {
        using var directory = new TestDirectory();
        var storage = new LocalStorageBackend("local", new LocalConnectionConfig { RootPath = directory.Path });

        var space = await storage.GetSpaceAsync();

        Assert.True(space.IsSuccess, space.Error?.ToString());
        Assert.True(space.Value!.TotalBytes > 0);
        Assert.InRange(space.Value.AvailableBytes!.Value, 0, space.Value.TotalBytes!.Value);
        Assert.True(storage.Capabilities.Supports(StorageFeature.SpaceInfo));
    }

    [Fact]
    public async Task Connections_without_command_support_say_so()
    {
        using var directory = new TestDirectory();
        var storage = new LocalStorageBackend("local", new LocalConnectionConfig { RootPath = directory.Path });

        Assert.Equal(StorageErrors.UnsupportedCode, (await storage.ExecuteCommandAsync("ls")).Error?.Code);
    }
}
