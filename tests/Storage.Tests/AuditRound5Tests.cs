using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Providers;
using CL.Storage.Providers.Local;
using CL.Storage.Providers.S3;
using CL.Storage.Providers.Sftp;
using CL.Storage.Providers.Swift;
using CodeLogic.Core.Results;
using Xunit;

namespace Storage.Tests;

// ── Audit round 5: Swift storage URL scheme ────────────────────────────────────────

public sealed class SwiftStorageUrlSchemeTests
{
    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage TempAuthReply(string storageUrl)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("X-Auth-Token", "tok");
        response.Headers.TryAddWithoutValidation("X-Storage-Url", storageUrl);
        return response;
    }

    private static HttpResponseMessage KeystoneReply(string storageUrl)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent(
                $$$"""{"token":{"expires_at":"2030-01-01T00:00:00Z","catalog":[{"type":"object-store","endpoints":[{"interface":"public","region":"r1","url":"{{{storageUrl}}}"}]}]}}""",
                Encoding.UTF8, "application/json")
        };
        response.Headers.TryAddWithoutValidation("X-Subject-Token", "tok");
        return response;
    }

    private static HttpResponseMessage StorageReply()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        response.Content.Headers.ContentLength = 0;
        return response;
    }

    [Theory]
    [InlineData(SwiftAuthenticationMode.TempAuthV1)]
    [InlineData(SwiftAuthenticationMode.KeystoneV3Password)]
    public async Task A_plain_http_storage_url_returned_by_the_server_is_refused(SwiftAuthenticationMode mode)
    {
        var handler = new RecordingHandler(request => request.RequestUri!.Host == "auth.example"
            ? mode == SwiftAuthenticationMode.TempAuthV1 ? TempAuthReply("http://storage.example/v1/AUTH_t") : KeystoneReply("http://storage.example/v1/AUTH_t")
            : StorageReply());
        await using var backend = new SwiftStorageBackend("swift", new HttpClient(handler), Config(mode, allowInsecureHttp: false), ownsClient: true);

        var result = await backend.GetInfoAsync("file.txt");

        Assert.True(result.IsFailure);
        Assert.Equal(StorageErrors.ProviderErrorCode, result.Error!.Code);
        Assert.DoesNotContain(handler.Requests, uri => uri.Host == "storage.example"); // the token never travelled over http
    }

    [Theory]
    [InlineData(SwiftAuthenticationMode.TempAuthV1)]
    [InlineData(SwiftAuthenticationMode.KeystoneV3Password)]
    public async Task A_returned_https_storage_url_is_used_and_http_is_accepted_only_with_AllowInsecureHttp(SwiftAuthenticationMode mode)
    {
        var secureHandler = new RecordingHandler(request => request.RequestUri!.Host == "auth.example"
            ? mode == SwiftAuthenticationMode.TempAuthV1 ? TempAuthReply("https://storage.example/v1/AUTH_t") : KeystoneReply("https://storage.example/v1/AUTH_t")
            : StorageReply());
        await using (var backend = new SwiftStorageBackend("swift", new HttpClient(secureHandler), Config(mode, allowInsecureHttp: false), ownsClient: true))
        {
            var result = await backend.GetInfoAsync("file.txt");
            Assert.True(result.IsSuccess, result.Error?.ToString());
            Assert.Contains(secureHandler.Requests, uri => uri.Host == "storage.example" && uri.Scheme == "https");
        }

        var insecureHandler = new RecordingHandler(request => request.RequestUri!.Host == "auth.example"
            ? mode == SwiftAuthenticationMode.TempAuthV1 ? TempAuthReply("http://storage.example/v1/AUTH_t") : KeystoneReply("http://storage.example/v1/AUTH_t")
            : StorageReply());
        await using (var backend = new SwiftStorageBackend("swift", new HttpClient(insecureHandler), Config(mode, allowInsecureHttp: true), ownsClient: true))
        {
            var result = await backend.GetInfoAsync("file.txt");
            Assert.True(result.IsSuccess, result.Error?.ToString());
            Assert.Contains(insecureHandler.Requests, uri => uri.Host == "storage.example" && uri.Scheme == "http");
        }
    }

    private static SwiftConnectionConfig Config(SwiftAuthenticationMode mode, bool allowInsecureHttp) => new()
    {
        Container = "c",
        AuthenticationMode = mode,
        AuthenticationUrl = allowInsecureHttp ? "http://auth.example/v3" : "https://auth.example/v3",
        Username = "user",
        Password = "pw",
        ProjectName = "proj",
        AllowInsecureHttp = allowInsecureHttp
    };
}

// ── Audit round 5: object-store keys the library cannot address ────────────────────

public sealed class ObjectKeyPathTests
{
    [Theory]
    [InlineData("", "a/../b", null)]
    [InlineData("", "../x", null)]
    [InlineData("", "a//b", null)]
    [InlineData("", "a/./b", null)]
    [InlineData("", "./a", null)]
    [InlineData("", "a\u0000b", null)]
    [InlineData("", "a/b", "a/b")]
    [InlineData("", "a/b/", "a/b/")]
    [InlineData("", "", "")]
    [InlineData("p/", "p/a/b", "a/b")]
    [InlineData("p/", "p/a/b/", "a/b/")]
    [InlineData("p/", "p/", "")]
    [InlineData("p/", "p/../x", null)]
    [InlineData("p/", "q/a", null)]
    public void Keys_map_to_storage_paths_only_when_addressable(string prefix, string key, string? expected) =>
        Assert.Equal(expected, ObjectKeyPaths.FromKey(prefix, key));

    [Fact]
    public async Task S3_listing_skips_keys_the_library_cannot_address()
    {
        var fake = ProviderFakeS3.Create();
        fake.Put("../x", [1]);
        fake.Put("a/../y", [2]);
        fake.Put("a//z", [3]);
        fake.Put("ok.txt", [4]);
        fake.Put("dir/inner.txt", [5]);
        await using var backend = new S3StorageBackend("s3", fake.Client, "bucket");

        var recursive = await backend.ListAsync("", new StorageListOptions { Recursive = true, PageSize = 100 });
        var flat = await backend.ListAsync("", new StorageListOptions { Recursive = false, PageSize = 100 });

        Assert.True(recursive.IsSuccess, recursive.Error?.ToString());
        Assert.Equal(["dir", "dir/inner.txt", "ok.txt"], recursive.Value!.Items.Select(item => item.Path).Order(StringComparer.Ordinal));
        Assert.True(flat.IsSuccess, flat.Error?.ToString());
        Assert.All(flat.Value!.Items, item => Assert.True(StoragePath.Normalize(item.Path).IsSuccess && !item.Path.Contains("//")));
        Assert.DoesNotContain(flat.Value.Items, item => item.Path.Contains(".."));
    }
}

// ── Audit round 5: known_hosts wildcard patterns ───────────────────────────────────

public sealed class KnownHostsWildcardTests
{
    private static string Key() => Convert.ToBase64String([1, 2, 3, 4]);

    [Theory]
    [InlineData("*.example.com", "host.example.com", true)]
    [InlineData("*.example.com", "HOST.EXAMPLE.COM", true)]
    [InlineData("*.example.com", "example.com", false)]
    [InlineData("host?.example.com", "host1.example.com", true)]
    [InlineData("host?.example.com", "host12.example.com", false)]
    [InlineData("*.example.com,!bad.example.com", "bad.example.com", false)]
    [InlineData("[*.example.com]:2222", "[host.example.com]:2222", true)]
    public void Wildcard_patterns_match_as_openssh_does(string patterns, string hostToken, bool expected)
    {
        var entries = SshHostTrust.Parse([$"{patterns} ssh-ed25519 {Key()}"]);

        Assert.Equal(expected, Assert.Single(entries).Matches(hostToken));
    }

    [Fact]
    public void A_long_wildcard_pattern_is_answered_promptly()
    {
        var pattern = string.Concat(Enumerable.Repeat("*a", 40)) + "*b";
        var entries = SshHostTrust.Parse([$"{pattern} ssh-ed25519 {Key()}"]);
        var watch = Stopwatch.StartNew();

        var matched = Assert.Single(entries).Matches(new string('a', 200));

        Assert.False(matched);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }
}

// ── Audit round 5: local provider ──────────────────────────────────────────────────

public sealed class LocalProviderAuditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cl-storage-tests", Guid.NewGuid().ToString("N"));
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "cl-storage-outside", Guid.NewGuid().ToString("N"));

    public LocalProviderAuditTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { Directory.Delete(_outside, recursive: true); } catch { }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_link_leading_outside_the_root_reports_no_target(bool followLinks)
    {
        Directory.CreateDirectory(Path.Combine(_root, "links"));
        if (!TryCreateDirectoryLink(Path.Combine(_root, "links", "out"), _outside)) return;
        await using var backend = Backend(followLinks);

        var link = await backend.ReadLinkAsync("links/out");
        var listing = await backend.ListAsync("links");
        var info = await backend.GetInfoAsync("links/out");

        Assert.True(link.IsSuccess, link.Error?.ToString());
        Assert.Equal(string.Empty, link.Value!.RawTarget);
        Assert.Null(link.Value.StoragePath);
        var item = Assert.Single(listing.Value!.Items);
        Assert.Equal(StorageItemType.Link, item.ItemType);
        Assert.Null(item.LinkTarget);
        if (followLinks)
            Assert.Equal(StorageErrors.InvalidPathCode, info.Error?.Code); // following it would leave the root
        else
            Assert.Null(info.Value?.LinkTarget);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_link_inside_the_root_reports_its_target_whether_or_not_links_are_followed(bool followLinks)
    {
        // An in-root target names nothing beyond the mount, and sync's LinkHandling.Follow relies on it even when the
        // connection refuses to traverse links in paths.
        Directory.CreateDirectory(Path.Combine(_root, "real"));
        if (!TryCreateDirectoryLink(Path.Combine(_root, "alias"), Path.Combine(_root, "real"))) return;
        await using var backend = Backend(followLinks);

        var link = await backend.ReadLinkAsync("alias");
        var listed = Assert.Single((await backend.ListAsync("")).Value!.Items, item => item.Path == "alias");

        Assert.True(link.IsSuccess, link.Error?.ToString());
        Assert.NotEmpty(link.Value!.RawTarget);
        Assert.Equal("real", link.Value.StoragePath);
        Assert.Equal(StorageItemType.Link, listed.ItemType);
        Assert.Equal(link.Value.RawTarget, listed.LinkTarget);
    }

    [Fact]
    public async Task Recursive_delete_of_a_tree_holding_a_link_out_leaves_the_target_intact()
    {
        var keep = Path.Combine(_outside, "keep.txt");
        await File.WriteAllBytesAsync(keep, [1]);
        Directory.CreateDirectory(Path.Combine(_root, "tree", "sub"));
        await File.WriteAllBytesAsync(Path.Combine(_root, "tree", "sub", "file.txt"), [2]);
        if (!TryCreateDirectoryLink(Path.Combine(_root, "tree", "out"), _outside)) return;
        await using var backend = Backend(followLinks: false);

        var deleted = await backend.DeleteAsync("tree", new StorageDeleteOptions { Recursive = true });

        Assert.True(deleted.IsSuccess, deleted.Error?.ToString());
        Assert.False(Directory.Exists(Path.Combine(_root, "tree")));
        Assert.True(Directory.Exists(_outside), "the link target directory must survive");
        Assert.True(File.Exists(keep), "the link target's content must survive");
    }

    [Fact]
    public async Task FollowLinks_true_still_refuses_a_mid_path_link_that_leaves_the_root()
    {
        await File.WriteAllBytesAsync(Path.Combine(_outside, "secret.txt"), [7]);
        Directory.CreateDirectory(Path.Combine(_root, "links"));
        if (!TryCreateDirectoryLink(Path.Combine(_root, "links", "out"), _outside)) return;
        await using var backend = Backend(followLinks: true);

        var upload = await backend.UploadBytesAsync("links/out/escaped.bin", [1]);
        var exists = await backend.ExistsAsync("links/out/secret.txt");
        var download = await backend.DownloadBytesAsync("links/out/secret.txt");
        var listing = await backend.ListAsync("links/out");

        Assert.Equal(StorageErrors.InvalidPathCode, upload.Error?.Code);
        Assert.Equal(StorageErrors.InvalidPathCode, exists.Error?.Code);
        Assert.Equal(StorageErrors.InvalidPathCode, download.Error?.Code);
        Assert.Equal(StorageErrors.InvalidPathCode, listing.Error?.Code);
        Assert.False(File.Exists(Path.Combine(_outside, "escaped.bin")));
    }

    [WindowsFact]
    public async Task Windows_trailing_dots_and_spaces_name_the_same_file_and_respect_Overwrite_false()
    {
        await using var backend = Backend(followLinks: false);
        Assert.True((await backend.UploadBytesAsync("foo.txt", [1, 2, 3])).IsSuccess);

        foreach (var alias in new[] { "foo.txt.", "foo.txt ", "foo.txt. ." })
        {
            var result = await backend.UploadBytesAsync(alias, [9], new StorageUploadOptions { Overwrite = false });
            Assert.True(result.IsFailure, $"'{alias}' must not replace foo.txt");
            Assert.Equal(StorageErrors.ConflictCode, result.Error!.Code);
        }

        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(Path.Combine(_root, "foo.txt")));
        Assert.Equal(["foo.txt"], Directory.GetFiles(_root).Select(Path.GetFileName));
    }

    [WindowsFact]
    public async Task A_root_with_the_extended_path_prefix_works_and_stays_contained()
    {
        await using var backend = new LocalStorageBackend("ext", new LocalConnectionConfig { RootPath = @"\\?\" + _root, FollowLinks = false });

        var upload = await backend.UploadBytesAsync("dir/file.txt", [1, 2]);
        var info = await backend.GetInfoAsync("dir/file.txt");
        var listing = await backend.ListAsync("", new StorageListOptions { Recursive = true });
        var download = await backend.DownloadBytesAsync("dir/file.txt");
        var escape = await backend.UploadBytesAsync("../escape.txt", [3]);
        var deleted = await backend.DeleteAsync("dir", new StorageDeleteOptions { Recursive = true });

        Assert.True(upload.IsSuccess, upload.Error?.ToString());
        Assert.Equal("dir/file.txt", info.Value?.Path);
        Assert.Equal(["dir", "dir/file.txt"], listing.Value!.Items.Select(item => item.Path).Order(StringComparer.Ordinal));
        Assert.Equal([1, 2], download.Value);
        Assert.Equal(StorageErrors.InvalidPathCode, escape.Error?.Code);
        Assert.False(File.Exists(Path.Combine(Directory.GetParent(_root)!.FullName, "escape.txt")));
        Assert.True(deleted.IsSuccess, deleted.Error?.ToString());
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public void Path_comparisons_follow_the_probed_case_sensitivity_not_the_operating_system()
    {
        var swapped = LocalStorageBackend.SwapCase(_root);
        Assert.NotEqual(_root, swapped);

        var sensitive = new LocalPathResolver(_root, followLinks: false, caseInsensitive: false);
        var insensitive = new LocalPathResolver(_root, followLinks: false, caseInsensitive: true);

        Assert.Equal(StringComparison.Ordinal, sensitive.Comparison);
        Assert.Equal(StringComparison.OrdinalIgnoreCase, insensitive.Comparison);
        Assert.False(sensitive.IsContained(Path.Combine(swapped, "x")));
        Assert.True(insensitive.IsContained(Path.Combine(swapped, "x")));
        Assert.Null(sensitive.ToStoragePath(Path.Combine(swapped, "x")));
        Assert.Equal("x", insensitive.ToStoragePath(Path.Combine(swapped, "x")));
    }

    [Fact]
    public async Task Append_creates_a_missing_parent_like_an_upload_and_refuses_a_directory()
    {
        await using var backend = Backend(followLinks: false);
        Directory.CreateDirectory(Path.Combine(_root, "folder"));

        var created = await backend.AppendAsync("new/parent/log.txt", new MemoryStream([1, 2]));
        var again = await backend.AppendAsync("new/parent/log.txt", new MemoryStream([3]));
        var directory = await backend.AppendAsync("folder", new MemoryStream([1]));

        Assert.True(created.IsSuccess, created.Error?.ToString());
        Assert.True(again.IsSuccess, again.Error?.ToString());
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(Path.Combine(_root, "new", "parent", "log.txt")));
        Assert.Equal(StorageErrors.ConflictCode, directory.Error?.Code);
        Assert.True(Directory.Exists(Path.Combine(_root, "folder")));
    }

    private LocalStorageBackend Backend(bool followLinks) =>
        new("local", new LocalConnectionConfig { RootPath = _root, FollowLinks = followLinks });

    /// <summary>A directory link: a symbolic link, or on Windows a junction (needs no privilege). False when the OS refuses.</summary>
    internal static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateSymbolicLink(linkPath, targetPath);
                return true;
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
            using var process = Process.Start(startInfo);
            if (process is null) return false;
            process.WaitForExit();
            return process.ExitCode == 0 && Directory.Exists(linkPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

// ── Audit round 5: a remote listing that escapes the requested directory ───────────

public sealed class EscapingRemoteListingTests
{
    /// <summary>Delegates to a real backend, but answers listings with paths of the test's choosing.</summary>
    private sealed class ListingOverride(string connectionId, IStorageBackend inner, Func<string, StorageItem[]> listing) : IStorageBackend
    {
        public string ConnectionId => connectionId;
        public StorageProvider Provider => inner.Provider;
        public string Root => inner.Root;
        public StorageCapabilities Capabilities => inner.Capabilities;
        public Task<Result<StorageItem>> GetInfoAsync(string path, CancellationToken cancellationToken = default) => inner.GetInfoAsync(path, cancellationToken);
        public Task<Result<bool>> ExistsAsync(string path, CancellationToken cancellationToken = default) => inner.ExistsAsync(path, cancellationToken);
        public Task<Result<StoragePage>> ListAsync(string path, StorageListOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<StoragePage>.Success(new StoragePage(listing(path), null)));
        public Task<Result> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) => inner.CreateDirectoryAsync(path, cancellationToken);
        public Task<Result<StorageItem>> UploadAsync(string path, Stream source, StorageUploadOptions? options = null, CancellationToken cancellationToken = default) => inner.UploadAsync(path, source, options, cancellationToken);
        public Task<Result<StorageItem>> UploadBytesAsync(string path, byte[] content, StorageUploadOptions? options = null, CancellationToken cancellationToken = default) => inner.UploadBytesAsync(path, content, options, cancellationToken);
        public Task<Result<Stream>> DownloadAsync(string path, StorageDownloadOptions? options = null, CancellationToken cancellationToken = default) => inner.DownloadAsync(path, options, cancellationToken);
        public Task<Result<byte[]>> DownloadBytesAsync(string path, StorageDownloadOptions? options = null, CancellationToken cancellationToken = default) => inner.DownloadBytesAsync(path, options, cancellationToken);
        public Task<Result> DeleteAsync(string path, StorageDeleteOptions? options = null, CancellationToken cancellationToken = default) => inner.DeleteAsync(path, options, cancellationToken);
        public Task<Result> CopyAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default) => inner.CopyAsync(sourcePath, destinationPath, options, cancellationToken);
        public Task<Result> MoveAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default) => inner.MoveAsync(sourcePath, destinationPath, options, cancellationToken);
        public Task<Result> CheckHealthAsync(CancellationToken cancellationToken = default) => inner.CheckHealthAsync(cancellationToken);
        public bool TryGetNativeClient<TClient>([NotNullWhen(true)] out TClient? client) where TClient : class { client = null; return false; }
        public Task<Result<NativeConnectionLease<TClient>>> OpenNativeConnectionAsync<TClient>(CancellationToken cancellationToken = default) where TClient : class =>
            inner.OpenNativeConnectionAsync<TClient>(cancellationToken);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("tree/../x")]
    [InlineData("x")]
    [InlineData("tree2/x")]
    public async Task A_listing_item_outside_the_requested_directory_is_rejected_and_nothing_is_written(string escapingPath)
    {
        using var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);
        var sourceRoot = directory.CreateDirectory("src");
        var source = new LocalStorageBackend("Source", new LocalConnectionConfig { RootPath = sourceRoot, FollowLinks = false });
        await source.UploadBytesAsync("tree/good.txt", [1]);
        await source.UploadBytesAsync("x", [6]);
        await source.UploadBytesAsync("tree2/x", [6]);
        var hostile = new ListingOverride("Remote", source, _ =>
        [
            new StorageItem { Path = "tree/good.txt", Name = "good.txt", ItemType = StorageItemType.File, Size = 1 },
            new StorageItem { Path = escapingPath, Name = "x", ItemType = StorageItemType.File, Size = 1 }
        ]);
        Assert.True(library.RegisterBackend("Remote", hostile).IsSuccess);
        var destinationParent = directory.CreateDirectory("downloads");
        var destination = Path.Combine(destinationParent, "tree");

        var result = await library.DownloadDirectoryAsync("Remote", "tree", destination);

        Assert.True(result.IsFailure, "a listing that escapes the directory must fail the download");
        Assert.Equal(StorageErrors.ProviderErrorCode, result.Error!.Code);
        Assert.False(File.Exists(Path.Combine(destinationParent, "x")), "nothing may be written beside the destination");
        Assert.False(File.Exists(Path.Combine(directory.Path, "x")), "nothing may be written above the destination");
        Assert.DoesNotContain(Directory.EnumerateFiles(directory.Path, "x", SearchOption.AllDirectories), file => !file.StartsWith(sourceRoot, StringComparison.Ordinal));
    }
}
