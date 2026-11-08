using System.Security.Cryptography;
using CL.Storage;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Models;
using CodeLogic.Core.Configuration;
using CodeLogic.Core.Events;
using CodeLogic.Core.Localization;
using CodeLogic.Core.Logging;
using CodeLogic.Framework.Libraries;
using Xunit;

namespace Storage.Integration.Tests;

/// <summary>A started, runtime-only library: no configuration files, connections added at runtime.</summary>
internal sealed class LiveLibrary : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cl-storage-live", Guid.NewGuid().ToString("N"));

    public StorageLibrary Library { get; } = new(new StorageLibraryOptions { RuntimeOnly = true });

    public static async Task<LiveLibrary> StartAsync(params (string Id, object Config)[] connections)
    {
        var live = new LiveLibrary();
        Directory.CreateDirectory(live._root);
        var context = new LibraryContext
        {
            LibraryId = "CL.Storage",
            LibraryDirectory = live._root,
            ConfigDirectory = Path.Combine(live._root, "config"),
            LocalizationDirectory = Path.Combine(live._root, "localization"),
            LogsDirectory = Path.Combine(live._root, "logs"),
            DataDirectory = Path.Combine(live._root, "data"),
            Logger = new SilentLogger(),
            Configuration = new ConfigurationManager(Path.Combine(live._root, "config")),
            Localization = new LocalizationManager(Path.Combine(live._root, "localization")),
            Events = new EventBus()
        };
        await live.Library.OnConfigureAsync(context);
        await live.Library.OnInitializeAsync(context);
        await live.Library.OnStartAsync(context);
        foreach (var (id, config) in connections)
        {
            // A cloud emulator's bucket or container is created by whichever test uses it first; make sure
            // it exists here too, so these tests do not depend on running after one that creates it.
            if (config is S3ConnectionConfig or AzureBlobConnectionConfig or GoogleCloudConnectionConfig or SwiftConnectionConfig)
                await (await CloudEmulators.CreateAsync((StorageConnectionConfigBase)config)).DisposeAsync();
            var added = config switch
            {
                LocalConnectionConfig local => await live.Library.AddOrUpdateConnectionAsync(id, local),
                StorageConnectionConfigBase provider => await live.Library.AddOrUpdateConnectionAsync(id, provider),
                _ => throw new ArgumentException("Unsupported configuration.")
            };
            Assert.True(added.IsSuccess, added.Error?.ToString());
        }
        return live;
    }

    public async ValueTask DisposeAsync()
    {
        await Library.OnStopAsync();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class SilentLogger : ILogger
    {
        public void Trace(string message) { }
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
        public void Critical(string message, Exception? exception = null) { }
    }
}

/// <summary>Guaranteed transfers against real servers.</summary>
public sealed class GuaranteedTransferLiveTests
{
    private static byte[] Content(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)(i % 239))];

    [SftpFact]
    public Task Sftp_upload_resumes_from_staged_bytes() => ResumeUploadAsync(LiveServers.Sftp());

    [FtpFact]
    public Task Ftp_upload_resumes_from_staged_bytes() => ResumeUploadAsync(LiveServers.Ftp());

    private static async Task ResumeUploadAsync(StorageConnectionConfigBase config)
    {
        await using var storage = LiveServers.Create(config);
        var dir = $"resume-{Guid.NewGuid():N}";
        var content = Content(300_000);
        var options = new StorageUploadOptions
        {
            ConflictPolicy = StorageConflictPolicy.Resume,
            SourceLastModified = DateTimeOffset.UtcNow.AddMinutes(-1),
            Verify = true
        };
        try
        {
            using (var failing = new DroppingStream(content, dropAfter: 180_000))
                Assert.True((await storage.UploadAsync($"{dir}/big.bin", failing, options)).IsFailure);
            Assert.False((await storage.ExistsAsync($"{dir}/big.bin")).Value);

            using var retry = new MemoryStream(content);
            var resumed = await storage.UploadAsync($"{dir}/big.bin", retry, options);

            Assert.True(resumed.IsSuccess, resumed.Error?.ToString());
            Assert.Equal(content, (await storage.DownloadBytesAsync($"{dir}/big.bin")).Value!);
            var left = (await storage.ListAsync(dir, new StorageListOptions { IncludeInternal = true })).Value!.Items;
            Assert.Equal([$"{dir}/big.bin"], left.Select(item => item.Path));
        }
        finally
        {
            await storage.DeleteAsync(dir, new StorageDeleteOptions { Recursive = true, IgnoreMissing = true });
        }
    }

    // Skipped, not silently passed, when S3 is not configured
    [SftpAndS3Fact]
    public async Task Verified_copy_from_sftp_to_an_object_store()
    {
        await using var live = await LiveLibrary.StartAsync(("sftp", LiveServers.Sftp()), ("s3", CloudEmulators.S3()));
        var dir = $"verified-{Guid.NewGuid():N}";
        var content = Content(70_000);
        var sftp = live.Library.GetStorage("sftp");
        await sftp.UploadBytesAsync($"{dir}/f.bin", content);
        try
        {
            var report = await live.Library.CopyAsync("sftp", $"{dir}/f.bin", "s3", $"{dir}/f.bin", new StorageTransferOptions
            {
                Verify = true,
                ExpectedSha256 = Convert.ToHexStringLower(SHA256.HashData(content)),
                ExpectedSourceLength = content.Length
            });

            Assert.True(report.IsSuccess, report.Error?.ToString());
            Assert.NotNull(report.VerifiedBy);
            Assert.NotNull(report.DestinationETag);
            Assert.Equal(content, (await live.Library.GetStorage("s3").DownloadBytesAsync($"{dir}/f.bin")).Value!);
        }
        finally
        {
            await sftp.DeleteAsync(dir, new StorageDeleteOptions { Recursive = true, IgnoreMissing = true });
            await live.Library.GetStorage("s3").DeleteAsync(dir, new StorageDeleteOptions { Recursive = true, IgnoreMissing = true });
        }
    }

    [WebDavFact]
    public Task Streamed_write_to_webdav() => StreamedWriteAsync(LiveServers.Create(LiveServers.WebDav()));

    [S3Fact]
    public async Task Streamed_write_to_s3() => await StreamedWriteAsync(await CloudEmulators.CreateAsync(CloudEmulators.S3()));

    private static async Task StreamedWriteAsync(IStorageBackend storage)
    {
        await using var _ = storage;
        var path = $"streamed-{Guid.NewGuid():N}.bin";
        var content = Content(150_000);
        try
        {
            await using var writer = (await storage.OpenWriteAsync(path, new StorageUploadOptions { ExpectedLength = content.Length, Verify = true })).Value!;
            for (var offset = 0; offset < content.Length; offset += 10_000)
                await writer.WriteAsync(content.AsMemory(offset, Math.Min(10_000, content.Length - offset)));
            Assert.False((await storage.ExistsAsync(path)).Value);
            var committed = await writer.CommitAsync();

            Assert.True(committed.IsSuccess, committed.Error?.ToString());
            Assert.Equal(content, (await storage.DownloadBytesAsync(path)).Value!);
        }
        finally
        {
            await storage.DeleteAsync(path, new StorageDeleteOptions { IgnoreMissing = true });
        }
    }

    /// <summary>A seekable stream that drops the connection after a number of bytes.</summary>
    private sealed class DroppingStream(byte[] data, int dropAfter) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => Check(base.Read(buffer, offset, Math.Min(count, 16_384)));
        public override int Read(Span<byte> buffer) => Check(base.Read(buffer[..Math.Min(buffer.Length, 16_384)]));

        private int Check(int read)
        {
            if (Position > dropAfter) throw new IOException("The connection was reset.");
            return read;
        }
    }
}
