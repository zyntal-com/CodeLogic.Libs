using System.Text;
using System.Diagnostics;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Models;
using CL.Storage.Providers.Local;
using Xunit;

namespace Storage.Tests;

public sealed class LocalStorageBackendTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cl-storage-tests", Guid.NewGuid().ToString("N"));

    public LocalStorageBackendTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Crud_round_trips_content_and_reports_file_metadata()
    {
        await using var backend = CreateBackend();
        IStorageService storage = backend;

        Assert.True((await storage.CreateDirectoryAsync("docs")).IsSuccess);
        var upload = await storage.UploadBytesAsync("docs/hello.txt", Encoding.UTF8.GetBytes("hello"));
        var exists = await storage.ExistsAsync("docs//./hello.txt");
        var info = await storage.GetInfoAsync("docs/hello.txt");
        await using var download = (await storage.DownloadAsync("docs/hello.txt")).Value!;
        using var reader = new StreamReader(download, Encoding.UTF8, leaveOpen: true);

        Assert.True(upload.IsSuccess, upload.Error?.Message);
        Assert.True(exists.Value);
        Assert.Equal("hello", await reader.ReadToEndAsync());
        Assert.Equal("docs/hello.txt", info.Value!.Path);
        Assert.Equal("hello.txt", info.Value.Name);
        Assert.Equal(StorageItemType.File, info.Value.ItemType);
        Assert.Equal(5, info.Value.Size);
        Assert.NotNull(info.Value.LastModified);
        Assert.Equal("text/plain", info.Value.ContentType);
        Assert.Empty(info.Value.Metadata);
    }

    [Fact]
    public async Task Recursive_listing_is_paged_and_contains_directories_and_files()
    {
        await using var backend = CreateBackend();
        await backend.UploadBytesAsync("a/one.txt", [1]);
        await backend.UploadBytesAsync("a/b/two.bin", [2]);

        var first = await backend.ListAsync("", new StorageListOptions { Recursive = true, PageSize = 2 });
        var second = await backend.ListAsync("", new StorageListOptions
        {
            Recursive = true,
            PageSize = 10,
            ContinuationToken = first.Value!.ContinuationToken
        });
        var paths = first.Value.Items.Concat(second.Value!.Items).Select(item => item.Path).ToArray();

        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.NotNull(first.Value!.ContinuationToken);
        Assert.Null(second.Value!.ContinuationToken);
        Assert.Equal(new[] { "a", "a/b", "a/b/two.bin", "a/one.txt" }, paths);
    }

    [Fact]
    public async Task Recursive_delete_removes_a_nonempty_tree()
    {
        await using var backend = CreateBackend();
        await backend.UploadBytesAsync("tree/child/file.bin", [1, 2, 3]);

        var deleted = await backend.DeleteAsync("tree", new StorageDeleteOptions { Recursive = true });
        var exists = await backend.ExistsAsync("tree");

        Assert.True(deleted.IsSuccess, deleted.Error?.Message);
        Assert.False(exists.Value);
    }

    [Fact]
    public async Task Overwrite_false_reports_conflicts_without_changing_source_or_destination()
    {
        await using var backend = CreateBackend();
        await backend.UploadBytesAsync("source.bin", [1]);
        await backend.UploadBytesAsync("destination.bin", [2]);

        var upload = await backend.UploadBytesAsync("destination.bin", [3], new StorageUploadOptions { Overwrite = false });
        var copy = await backend.CopyAsync("source.bin", "destination.bin", new StorageTransferOptions { Overwrite = false });
        var move = await backend.MoveAsync("source.bin", "destination.bin", new StorageTransferOptions { Overwrite = false });

        Assert.All(new[] { upload.Error, copy.Error, move.Error }, error => Assert.Equal("storage.conflict", error!.Code));
        Assert.Equal(new byte[] { 2 }, (await backend.DownloadBytesAsync("destination.bin")).Value);
        Assert.True((await backend.ExistsAsync("source.bin")).Value);
    }

    [Fact]
    public async Task Same_path_copy_and_move_are_rejected_without_changing_the_file()
    {
        await using var backend = CreateBackend();
        await backend.UploadBytesAsync("same.bin", [1, 2, 3]);

        var copy = await backend.CopyAsync("same.bin", "./same.bin");
        var move = await backend.MoveAsync("same.bin", "same.bin");

        Assert.Equal("storage.invalid_path", copy.Error!.Code);
        Assert.Equal("storage.invalid_path", move.Error!.Code);
        Assert.Equal(new byte[] { 1, 2, 3 }, (await backend.DownloadBytesAsync("same.bin")).Value);
    }

    [Fact]
    public async Task Directory_transfer_into_its_own_descendant_is_rejected_without_mutation()
    {
        await using var backend = CreateBackend();
        await backend.UploadBytesAsync("tree/child.bin", [7]);

        var copy = await backend.CopyAsync("tree", "tree/nested/copy");
        var move = await backend.MoveAsync("tree", "tree/nested/move");

        Assert.Equal("storage.invalid_path", copy.Error!.Code);
        Assert.Equal("storage.invalid_path", move.Error!.Code);
        Assert.Equal(new byte[] { 7 }, (await backend.DownloadBytesAsync("tree/child.bin")).Value);
        Assert.False((await backend.ExistsAsync("tree/nested")).Value);
    }

    [Fact]
    public async Task Upload_does_not_dispose_the_caller_owned_stream()
    {
        await using var backend = CreateBackend();
        var source = new TrackingMemoryStream(Encoding.UTF8.GetBytes("owned by caller"));

        var result = await backend.UploadAsync("caller.txt", source);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.False(source.WasDisposed);
        source.Position = 0;
        Assert.Equal((byte)'o', source.ReadByte());
        source.Dispose();
    }

    [Fact]
    public async Task Failed_overwrite_upload_preserves_the_original_and_removes_staging_files()
    {
        await using var backend = CreateBackend();
        await backend.UploadBytesAsync("important.bin", [1, 2, 3, 4]);
        var source = new ThrowingReadStream([9, 9]);

        var result = await backend.UploadAsync(
            "important.bin",
            source,
            new StorageUploadOptions { Overwrite = true });

        Assert.True(result.IsFailure);
        Assert.Equal([1, 2, 3, 4], (await backend.DownloadBytesAsync("important.bin")).Value);
        Assert.Empty(Directory.EnumerateFiles(_root, ".cl-storage-*", SearchOption.AllDirectories));
        Assert.True(source.CanRead);
    }

    [Fact]
    public async Task Byte_download_enforces_the_operation_limit_and_constructor_default()
    {
        await using var backend = CreateBackend(maxBufferedBytes: 4);
        await backend.UploadBytesAsync("five.bin", [1, 2, 3, 4, 5]);

        var defaultLimit = await backend.DownloadBytesAsync("five.bin");
        var optionLimit = await backend.DownloadBytesAsync("five.bin", new StorageDownloadOptions { MaxBufferedBytes = 3 });
        var allowed = await backend.DownloadBytesAsync("five.bin", new StorageDownloadOptions { MaxBufferedBytes = 5 });

        Assert.Equal("storage.too_large", defaultLimit.Error!.Code);
        Assert.Equal("storage.too_large", optionLimit.Error!.Code);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, allowed.Value);
    }

    [Fact]
    public async Task Byte_download_follows_an_allowed_in_root_file_link()
    {
        var targetDirectory = Path.Combine(_root, "targets");
        Directory.CreateDirectory(targetDirectory);
        var targetPath = Path.Combine(targetDirectory, "target.bin");
        await File.WriteAllBytesAsync(targetPath, [1, 2, 3, 4]);
        var linkPath = Path.Combine(_root, "linked.bin");
        await using var backend = CreateBackend(followLinks: true);

        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            var result = await backend.DownloadBytesAsync("linked.bin");

            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, result.Value);
        }
        catch (Exception error) when (OperatingSystem.IsWindows() && error is UnauthorizedAccessException or IOException)
        {
            // Windows without Developer Mode cannot create file symlinks. A directory
            // junction has the same Size=null link metadata and still proves the crash.
            var metadataLink = Path.Combine(_root, "linked-directory");
            CreateDirectoryLink(metadataLink, targetDirectory);
            var result = await backend.DownloadBytesAsync("linked-directory");

            Assert.True(result.IsFailure);
        }
    }

    [Fact]
    public async Task Range_download_returns_only_the_requested_bytes()
    {
        await using var backend = CreateBackend();
        await backend.UploadBytesAsync("range.bin", [0, 1, 2, 3, 4]);

        var result = await backend.DownloadBytesAsync("range.bin", new StorageDownloadOptions { Offset = 1, Length = 3 });

        Assert.Equal(new byte[] { 1, 2, 3 }, result.Value);
    }

    [Fact]
    public async Task Precancelled_operations_propagate_cancellation()
    {
        await using var backend = CreateBackend();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backend.ExistsAsync("anything", cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backend.UploadBytesAsync("anything", [1], cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Traversal_is_rejected_before_touching_the_filesystem()
    {
        await using var backend = CreateBackend();

        var result = await backend.UploadBytesAsync("../outside.bin", [1]);

        Assert.Equal("storage.invalid_path", result.Error!.Code);
        Assert.False(File.Exists(Path.Combine(Directory.GetParent(_root)!.FullName, "outside.bin")));
    }

    [Fact]
    public async Task FollowLinks_false_rejects_a_link_that_would_leave_the_root()
    {
        var outside = Path.Combine(Path.GetTempPath(), "cl-storage-outside", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(Path.Combine(_root, "links"));
        try
        {
            CreateDirectoryLink(Path.Combine(_root, "links", "outside"), outside);

            await using var backend = CreateBackend(followLinks: false);
            var result = await backend.UploadBytesAsync("links/outside/escaped.bin", [1]);

            Assert.Equal("storage.invalid_path", result.Error!.Code);
            Assert.False(File.Exists(Path.Combine(outside, "escaped.bin")));
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Recursive_listing_with_follow_links_does_not_revisit_a_directory_cycle()
    {
        var directory = Path.Combine(_root, "cycle");
        Directory.CreateDirectory(directory);
        CreateDirectoryLink(Path.Combine(directory, "self"), directory);
        await using var backend = CreateBackend(followLinks: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var result = await backend.ListAsync("", new StorageListOptions { Recursive = true }, timeout.Token);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(new[] { "cycle", "cycle/self" }, result.Value!.Items.Select(item => item.Path));
    }

    [Fact]
    public async Task UNC_compatible_roots_are_canonicalized_without_provider_special_cases()
    {
        const string uncRoot = @"\\storage-server\mounted-share\app-data";
        await using var backend = new LocalStorageBackend("mounted", new LocalConnectionConfig { RootPath = uncRoot });

        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(uncRoot)), backend.Root);
        Assert.Equal(StorageProvider.Local, backend.Provider);
    }

    [Fact]
    public async Task Health_is_scoped_to_the_configured_root()
    {
        await using var healthy = CreateBackend();
        await using var missing = new LocalStorageBackend("missing", new LocalConnectionConfig
        {
            RootPath = Path.Combine(_root, "does-not-exist")
        });

        Assert.True((await healthy.CheckHealthAsync()).IsSuccess);
        Assert.Equal("storage.not_found", (await missing.CheckHealthAsync()).Error!.Code);
        Assert.False(healthy.TryGetNativeClient<object>(out _));
        Assert.Equal("storage.unsupported", (await healthy.OpenNativeConnectionAsync<object>()).Error!.Code);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Paging_a_large_recursive_listing_yields_each_entry_once_in_stable_order()
    {
        // End-to-end guard for the shared listing cursor: a small page size over a folder with many
        // entries must not drop, duplicate, or reorder anything.
        await using var backend = CreateBackend();
        IStorageService storage = backend;
        var expected = new List<string>();
        for (var directory = 0; directory < 20; directory++)
        {
            var name = $"dir-{directory:D3}";
            Directory.CreateDirectory(Path.Combine(_root, name));
            expected.Add(name);
            for (var file = 0; file < 25; file++)
            {
                var relative = $"{name}/file-{file:D3}.txt";
                await File.WriteAllTextAsync(Path.Combine(_root, name, $"file-{file:D3}.txt"), "x");
                expected.Add(relative);
            }
        }

        var delivered = new List<string>();
        var tokens = new List<string>();
        string? token = null;
        do
        {
            var page = await storage.ListAsync(string.Empty, new StorageListOptions
            {
                Recursive = true,
                PageSize = 40,
                ContinuationToken = token
            });

            Assert.True(page.IsSuccess, page.Error?.Message);
            delivered.AddRange(page.Value!.Items.Select(item => item.Path));
            token = page.Value.ContinuationToken;
            if (token is not null) tokens.Add(token);
        }
        while (token is not null);

        expected.Sort(StringComparer.Ordinal);
        Assert.Equal(expected, delivered);
        Assert.Equal(tokens.Count, tokens.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("file.txt:hidden.exe")]
    [InlineData("data.json::$DATA")]
    [InlineData("docs/file.txt:stream")]
    [InlineData("dir:stream/child.txt")]
    public async Task Windows_alternate_data_stream_paths_are_rejected(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var backend = CreateBackend();
        await backend.UploadBytesAsync("file.txt", [1]);
        await backend.UploadBytesAsync("data.json", [1]);

        var upload = await backend.UploadBytesAsync(path, [9, 9, 9]);
        var exists = await backend.ExistsAsync(path);

        Assert.True(upload.IsFailure);
        Assert.Equal(CL.Storage.Errors.StorageErrors.InvalidPathCode, upload.Error!.Code);
        Assert.True(exists.IsFailure);
        Assert.Equal(CL.Storage.Errors.StorageErrors.InvalidPathCode, exists.Error!.Code);
    }

    [Fact]
    public void Windows_drive_relative_paths_do_not_resolve_against_the_process_directory()
    {
        if (!OperatingSystem.IsWindows()) return;
        // Root at the process directory: before the fix "C:name" resolved against the current directory of drive C
        // (the process directory), passed the containment check, and pointed at a different file than root/"C:name".
        var cwd = Directory.GetCurrentDirectory();
        var drive = Path.GetPathRoot(cwd)![0];
        var resolver = new LocalPathResolver(cwd, followLinks: false);

        var resolved = resolver.Resolve($"{drive}:drive-relative.txt");
        var rooted = resolver.Resolve($"{drive}:/Windows/win.ini");

        Assert.True(resolved.IsFailure);
        Assert.Equal(CL.Storage.Errors.StorageErrors.InvalidPathCode, resolved.Error!.Code);
        Assert.True(rooted.IsFailure);
        Assert.Equal(CL.Storage.Errors.StorageErrors.InvalidPathCode, rooted.Error!.Code);
    }

    [Fact]
    public void Colons_stay_valid_in_portable_paths_for_remote_providers()
    {
        // Colons are legal in S3 keys and on POSIX servers, so StoragePath itself keeps them; only the local
        // provider on Windows rejects them.
        var normalized = StoragePath.Normalize("logs/2026-10-31T12:00:00.json");

        Assert.True(normalized.IsSuccess);
        Assert.Equal("logs/2026-10-31T12:00:00.json", normalized.Value);
    }

    private LocalStorageBackend CreateBackend(bool followLinks = false, long maxBufferedBytes = 67_108_864) =>
        new("LocalTest", new LocalConnectionConfig
        {
            RootPath = _root,
            FollowLinks = followLinks
        }, maxBufferedBytes);

    private static void CreateDirectoryLink(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return;
        }

        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(linkPath);
        startInfo.ArgumentList.Add(targetPath);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start mklink.");
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd());
    }

    private sealed class TrackingMemoryStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool WasDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class ThrowingReadStream(byte[] firstChunk) : Stream
    {
        private bool _returnedChunk;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_returnedChunk)
                return ValueTask.FromException<int>(new IOException("simulated source failure"));
            _returnedChunk = true;
            var count = Math.Min(firstChunk.Length, buffer.Length);
            firstChunk.AsSpan(0, count).CopyTo(buffer.Span);
            return ValueTask.FromResult(count);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
