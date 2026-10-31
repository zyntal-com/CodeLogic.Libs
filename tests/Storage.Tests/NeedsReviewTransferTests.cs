using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Events;
using CL.Storage.Models;
using CL.Storage.Providers.Local;
using CL.Storage.Registry;
using CodeLogic.Core.Events;
using CodeLogic.Core.Results;
using Xunit;

namespace Storage.Tests;

/// <summary>Transfer findings of the round-3 review (needs-review.md, sections A, B and C).</summary>
public sealed class NeedsReviewTransferTests
{
    internal static async Task<(global::CL.Storage.StorageLibrary Library, TestDirectory Directory, string A, string B)> TwoConnectionsAsync(IEventBus? events = null)
    {
        var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.CreateDirectory("library"), events);
        var library = new global::CL.Storage.StorageLibrary();
        var a = directory.CreateDirectory("a");
        var b = directory.CreateDirectory("b");
        await StorageLibraryTestSupport.InitializeAsync(library, context, configureLocal: local =>
        {
            local.Connections["Default"] = new() { RootPath = a };
            local.Connections["B"] = new() { RootPath = b };
        });
        return (library, directory, a, b);
    }

    internal static async Task<List<string>> ListAllAsync(IStorageService storage, string path = "")
    {
        var page = await storage.ListAsync(path, new StorageListOptions { Recursive = true, IncludeInternal = true });
        return [.. page.Value!.Items.Select(item => item.Path).Order()];
    }

    internal static LocalStorageBackend Local(string root, string id = "L") => new(id, new LocalConnectionConfig { RootPath = root });

    internal static byte[] Content(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)(i % 251))];

    /// <summary>A source whose every attempt reads <paramref name="content"/> from the requested offset.</summary>
    internal static FakeStorageBackend Source(byte[] content, Func<int, byte[], Stream>? stream = null, string? eTag = "\"v1\"", Func<long, Result<Stream>?>? refuse = null)
    {
        var attempts = 0;
        return new FakeStorageBackend(
            "Src",
            getInfo: (path, _) => Task.FromResult(Result<StorageItem>.Success(new StorageItem
            {
                Path = path, Name = path, ItemType = StorageItemType.File, Size = content.Length, ETag = eTag
            })),
            downloadWithOptions: (_, options, _) =>
            {
                var offset = options?.Offset ?? 0;
                if (refuse?.Invoke(offset) is { } refused)
                    return Task.FromResult(refused);
                var rest = content[(int)offset..];
                var attempt = Interlocked.Increment(ref attempts);
                return Task.FromResult(Result<Stream>.Success(stream?.Invoke(attempt, rest) ?? new MemoryStream(rest)));
            });
    }

    // ---------------------------------------------------------------- A1

    [Fact] // needs-review A1
    public async Task A_directory_move_keeps_files_added_or_changed_on_the_source_during_the_copy()
    {
        var (library, directory, _, _) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var local = Local(directory.CreateDirectory("src"));
        await local.UploadBytesAsync("tree/a.txt", [1]);
        await local.UploadBytesAsync("tree/b.txt", [2]);
        await local.UploadBytesAsync("tree/sub/c.txt", [3]);
        var source = new InterceptBackend(local)
        {
            Download = async (path, options, token) =>
            {
                if (path == "tree/a.txt")
                {
                    // Someone adds a file and rewrites another while the move copies.
                    await local.UploadBytesAsync("tree/new.txt", [4]);
                    await local.UploadBytesAsync("tree/b.txt", [2, 2, 2]);
                }
                return await local.DownloadAsync(path, options, token);
            }
        };
        Assert.True(library.RegisterBackend("Src", source.Named("Src")).IsSuccess);

        var report = await library.MoveAsync("Src", "tree", "B", "moved");

        Assert.Equal(StorageTransferOutcome.NeedsReconciliation, report.Outcome);
        Assert.False(report.SourceDeleted);
        Assert.True(report.DestinationCommitted);
        Assert.Equal([4], (await local.DownloadBytesAsync("tree/new.txt")).Value!);
        Assert.Equal([2, 2, 2], (await local.DownloadBytesAsync("tree/b.txt")).Value!);
        Assert.False((await local.ExistsAsync("tree/a.txt")).Value);
        Assert.False((await local.ExistsAsync("tree/sub")).Value, "a folder emptied by the move is removed");
        Assert.Equal([1], (await library.GetStorage("B").DownloadBytesAsync("moved/a.txt")).Value!);
        Assert.Equal([3], (await library.GetStorage("B").DownloadBytesAsync("moved/sub/c.txt")).Value!);
    }

    [Fact] // needs-review A1
    public async Task A_complete_directory_move_deletes_the_source_file_by_file()
    {
        var (library, directory, _, _) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var local = Local(directory.CreateDirectory("src"));
        await local.UploadBytesAsync("tree/a.txt", [1]);
        await local.UploadBytesAsync("tree/sub/c.txt", [3]);
        var deletes = new ConcurrentQueue<(string Path, bool Recursive)>();
        var source = new InterceptBackend(local)
        {
            Delete = (path, options, token) =>
            {
                deletes.Enqueue((path, options?.Recursive == true));
                return local.DeleteAsync(path, options, token);
            }
        };
        Assert.True(library.RegisterBackend("Src", source.Named("Src")).IsSuccess);

        var report = await library.MoveAsync("Src", "tree", "B", "moved");

        Assert.Equal(StorageTransferOutcome.Completed, report.Outcome);
        Assert.True(report.SourceDeleted);
        Assert.False((await local.ExistsAsync("tree")).Value);
        Assert.DoesNotContain(deletes, delete => delete.Recursive);
        Assert.Contains(deletes, delete => delete.Path == "tree/a.txt");
    }

    // ---------------------------------------------------------------- A2

    [Fact] // needs-review A2
    public async Task A_native_move_that_copies_and_deletes_is_pinned_to_the_version_read_and_checked_just_before_when_it_cannot_be()
    {
        var (library, directory, _, _) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var local = Local(directory.CreateDirectory("obj"));
        await local.UploadBytesAsync("f.bin", [1, 2, 3]);
        var seen = (await local.GetInfoAsync("f.bin")).Value!;
        StorageTransferOptions? native = null;
        var unpinned = 0;
        var store = new InterceptBackend(local)
        {
            // An object store: its move is a copy and a delete, not an atomic rename.
            CapabilitiesMap = capabilities => new StorageCapabilities(capabilities.Features & ~StorageFeature.AtomicMove),
            Move = (from, to, options, token) =>
            {
                if (from != "f.bin") return local.MoveAsync(from, to, options, token);
                if (options?.ExpectedSourceETag is null)
                {
                    unpinned++;
                    return local.MoveAsync(from, to, options, token);
                }
                native = options;
                return Task.FromResult(Result.Failure(StorageErrors.Unsupported("This connection cannot pin a move to a version.")));
            }
        };
        Assert.True(library.RegisterBackend("Obj", store.Named("Obj")).IsSuccess);

        var report = await library.MoveAsync("Obj", "f.bin", "Obj", "g.bin");

        Assert.Equal(seen.ETag, native?.ExpectedSourceETag);
        // needs-review R4-A1: refused the pin, the source is compared just before the server's own move (no relay).
        Assert.Equal(1, unpinned);
        Assert.Equal(StorageConditionEnforcement.CheckedBeforeCommit, report.ConditionEnforcement);
        Assert.True(report.IsSuccess, report.Error?.ToString());
        Assert.True(report.SourceDeleted);
        Assert.Equal([1, 2, 3], (await local.DownloadBytesAsync("g.bin")).Value!);
        Assert.False((await local.ExistsAsync("f.bin")).Value);
    }

    // ---------------------------------------------------------------- A3

    [Fact] // needs-review A3
    public async Task A_directory_moved_onto_an_existing_directory_merges_instead_of_replacing_it()
    {
        var (library, directory, _, _) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var local = Local(directory.CreateDirectory("ftp"));
        await local.UploadBytesAsync("tree/a.txt", [1]);
        await local.UploadBytesAsync("moved/keep.txt", [9]);
        var ftp = new InterceptBackend(local)
        {
            Move = async (from, to, options, token) =>
            {
                // What FTP, SFTP and WebDAV did: the existing destination directory is replaced.
                if (from == "tree")
                    await local.DeleteAsync(to, new StorageDeleteOptions { Recursive = true, IgnoreMissing = true }, token);
                return await local.MoveAsync(from, to, options, token);
            }
        };
        Assert.True(library.RegisterBackend("Ftp", ftp.Named("Ftp")).IsSuccess);

        var report = await library.MoveAsync("Ftp", "tree", "Ftp", "moved");

        Assert.True(report.IsSuccess, report.Error?.ToString());
        Assert.Equal([9], (await local.DownloadBytesAsync("moved/keep.txt")).Value!);
        Assert.Equal([1], (await local.DownloadBytesAsync("moved/a.txt")).Value!);
        Assert.False((await local.ExistsAsync("tree")).Value);
    }

    // ---------------------------------------------------------------- A4

    [Fact] // needs-review A4
    public async Task A_resume_token_does_not_turn_on_overwrite()
    {
        var (library, directory, _, _) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var content = Content(200_000);
        Assert.True(library.RegisterBackend("Src", Source(content, (attempt, rest) => attempt == 1 ? new FailingStream(rest, failAfter: 120_000) : new MemoryStream(rest))).IsSuccess);
        var first = await library.CopyAsync("Src", "big.bin", "B", "big.bin", new StorageTransferOptions { ConflictPolicy = StorageConflictPolicy.Resume });
        Assert.NotNull(first.ResumeToken);
        // Someone else creates the destination before the token is replayed without overwrite.
        await library.GetStorage("B").UploadBytesAsync("big.bin", [7, 7, 7]);

        var replay = await library.CopyAsync("Src", "big.bin", "B", "big.bin", new StorageTransferOptions { Overwrite = false, ResumeToken = first.ResumeToken });

        Assert.False(replay.IsSuccess);
        Assert.Equal(StorageErrors.InvalidContentCode, replay.Error?.Code);
        Assert.Equal([7, 7, 7], (await library.GetStorage("B").DownloadBytesAsync("big.bin")).Value!);
    }

    // ---------------------------------------------------------------- A5

    [Fact] // needs-review A5
    public async Task Two_transfers_of_one_source_to_one_destination_do_not_write_into_one_part_file()
    {
        var (library, directory, _, _) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var content = Content(300_000);
        var firstAtGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(library.RegisterBackend("Src", Source(content, (attempt, rest) =>
        {
            if (attempt == 1) return new GatedStream(rest, 100_000, firstAtGate, secondOpened.Task);
            secondOpened.TrySetResult();
            return new MemoryStream(rest);
        })).IsSuccess);
        var options = new StorageTransferOptions { ConflictPolicy = StorageConflictPolicy.Resume };

        var first = library.CopyAsync("Src", "big.bin", "B", "big.bin", options);
        await firstAtGate.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = await library.CopyAsync("Src", "big.bin", "B", "big.bin", options);
        var firstReport = await first.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(firstReport.IsSuccess, firstReport.Error?.ToString());
        Assert.True(second.IsSuccess, second.Error?.ToString());
        Assert.Equal(content, (await library.GetStorage("B").DownloadBytesAsync("big.bin")).Value!);
        Assert.Equal(["big.bin"], await ListAllAsync(library.GetStorage("B")));
    }

    [Fact] // needs-review A5
    public async Task A_destination_that_does_not_hold_the_committed_length_always_fails_even_without_verify()
    {
        var (library, directory, _, b) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var local = Local(b);
        var hooked = new InterceptBackend(local)
        {
            Move = async (from, to, options, token) =>
            {
                var moved = await local.MoveAsync(from, to, options, token);
                if (to == "t.bin") await local.UploadBytesAsync("t.bin", [7, 7, 7, 7]);
                return moved;
            }
        };
        Assert.True(library.RegisterBackend("H", hooked.Named("H")).IsSuccess);
        await library.GetStorage("Default").UploadBytesAsync("n.bin", [9, 9]);

        var report = await library.CopyAsync("Default", "n.bin", "H", "t.bin");

        Assert.Equal(StorageTransferOutcome.NeedsReconciliation, report.Outcome);
        Assert.True(report.DestinationCommitted);
        Assert.True(StorageErrorInfo.DestinationCommitted(report.Error));
        Assert.Equal([7, 7, 7, 7], (await local.DownloadBytesAsync("t.bin")).Value!);
    }

    // ---------------------------------------------------------------- A6

    [Fact] // needs-review A6
    public async Task A_part_file_whose_size_cannot_be_read_is_kept_and_not_appended_after()
    {
        var (library, directory, _, b) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var content = Content(200_000);
        Assert.True(library.RegisterBackend("Src", Source(content, (attempt, rest) => attempt == 1 ? new FailingStream(rest, failAfter: 120_000) : new MemoryStream(rest))).IsSuccess);
        var local = Local(b);
        var failPartInfo = false;
        var destination = new InterceptBackend(local)
        {
            GetInfo = (path, token) =>
            {
                if (failPartInfo && path.Contains(StagedWriter.ResumablePrefix, StringComparison.Ordinal))
                {
                    failPartInfo = false;
                    return Task.FromResult(Result<StorageItem>.Failure(StorageErrors.Unavailable("The server is briefly unavailable.")));
                }
                return local.GetInfoAsync(path, token);
            }
        };
        Assert.True(library.RegisterBackend("D", destination.Named("D")).IsSuccess);
        var options = new StorageTransferOptions { ConflictPolicy = StorageConflictPolicy.Resume };
        var first = await library.CopyAsync("Src", "big.bin", "D", "big.bin", options);
        Assert.NotNull(first.ResumeToken);

        failPartInfo = true;
        var second = await library.CopyAsync("Src", "big.bin", "D", "big.bin", options with { ResumeToken = first.ResumeToken });
        var third = await library.CopyAsync("Src", "big.bin", "D", "big.bin", options with { ResumeToken = second.ResumeToken ?? first.ResumeToken });

        Assert.False(second.IsSuccess);
        Assert.NotNull(second.ResumeToken);
        Assert.True(third.IsSuccess, third.Error?.ToString());
        Assert.Equal(content, (await local.DownloadBytesAsync("big.bin")).Value!);
    }

    // ---------------------------------------------------------------- A7

    [Fact] // needs-review A7
    public async Task A_fully_staged_resume_does_not_open_the_source_at_its_end()
    {
        var (library, directory, _, _) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var content = Content(100_000);
        Assert.True(library.RegisterBackend("Src", Source(
            content,
            (attempt, rest) => attempt == 1 ? new FailingAtEndStream(rest) : new MemoryStream(rest),
            // A range starting at the end is what S3 and Azure answer with 416.
            refuse: offset => offset >= content.Length
                ? Result<Stream>.Failure(StorageErrors.InvalidPath("The requested range is not satisfiable.", "httpStatus=416"))
                : (Result<Stream>?)null)).IsSuccess);
        var options = new StorageTransferOptions { ConflictPolicy = StorageConflictPolicy.Resume };
        var first = await library.CopyAsync("Src", "big.bin", "B", "big.bin", options);
        Assert.Equal(content.Length, first.ResumeToken?.BytesStaged);

        var second = await library.CopyAsync("Src", "big.bin", "B", "big.bin", options with { ResumeToken = first.ResumeToken });

        Assert.True(second.IsSuccess, second.Error?.ToString());
        Assert.Equal(content.Length, second.BytesResumed);
        Assert.Equal(content, (await library.GetStorage("B").DownloadBytesAsync("big.bin")).Value!);
    }

    // ---------------------------------------------------------------- A8

    [Fact] // needs-review A8
    public async Task A_verify_mismatch_after_commit_is_reported_and_never_rolled_back_over_another_write()
    {
        var (library, directory, _, b) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var local = Local(b);
        await local.UploadBytesAsync("target.bin", [1]);
        var hooked = new InterceptBackend(local)
        {
            Move = async (from, to, options, token) =>
            {
                var moved = await local.MoveAsync(from, to, options, token);
                // Another writer replaces the destination right after the commit.
                if (to == "target.bin") await local.UploadBytesAsync("target.bin", [7, 7, 7, 7]);
                return moved;
            }
        };
        Assert.True(library.RegisterBackend("H", hooked.Named("H")).IsSuccess);
        await library.GetStorage("Default").UploadBytesAsync("new.bin", [9, 9]);

        var report = await library.CopyAsync("Default", "new.bin", "H", "target.bin", new StorageTransferOptions { Verify = true });

        Assert.Equal(StorageTransferOutcome.NeedsReconciliation, report.Outcome);
        Assert.True(report.DestinationCommitted);
        Assert.Equal([7, 7, 7, 7], (await local.DownloadBytesAsync("target.bin")).Value!);
        // needs-review R4-A4: the previous version is kept and named, never deleted.
        Assert.NotNull(report.BackupLeftBehind);
        Assert.Equal([1], (await local.DownloadBytesAsync(report.BackupLeftBehind!)).Value!);
        Assert.Equal([report.BackupLeftBehind!, "target.bin"], await ListAllAsync(local));
    }

    [Fact] // needs-review A8
    public async Task A_directory_rollback_does_not_undo_a_file_someone_changed_after_it_was_committed()
    {
        var (library, directory, _, b) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var local = Local(b);
        var hooked = new InterceptBackend(local)
        {
            Move = async (from, to, options, token) =>
            {
                if (to == "out/b.txt")
                    return Result.Failure(StorageErrors.Unavailable("The second file cannot be committed."));
                var moved = await local.MoveAsync(from, to, options, token);
                if (to == "out/a.txt") await local.UploadBytesAsync("out/a.txt", [5, 5, 5]);
                return moved;
            }
        };
        Assert.True(library.RegisterBackend("H", hooked.Named("H")).IsSuccess);
        await library.GetStorage("Default").UploadBytesAsync("tree/a.txt", [1]);
        await library.GetStorage("Default").UploadBytesAsync("tree/b.txt", [2]);

        var report = await library.CopyAsync("Default", "tree", "H", "out");

        Assert.False(report.IsSuccess);
        Assert.Equal([5, 5, 5], (await local.DownloadBytesAsync("out/a.txt")).Value!);
    }

    // ---------------------------------------------------------------- A9 (and E: the condition test's concurrent delete)

    [Fact] // needs-review A9
    public async Task A_destination_deleted_while_its_condition_is_checked_is_not_brought_back()
    {
        var (library, directory, _, b) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var local = Local(b, "H");
        await local.UploadBytesAsync("target.bin", [1]);
        var seen = (await local.GetInfoAsync("target.bin")).Value!;
        // Someone deletes the destination right after the transfer backed it up.
        var hooked = new HookedBackend(local, async (_, destination) =>
        {
            if (destination.Contains(".cl-storage-transfer-backup-", StringComparison.Ordinal))
                await local.DeleteAsync("target.bin");
        });
        Assert.True(library.RegisterBackend("H", hooked).IsSuccess);
        await library.GetStorage("Default").UploadBytesAsync("new.bin", [9, 9]);

        var report = await library.CopyAsync("Default", "new.bin", "H", "target.bin",
            new StorageTransferOptions { DestinationCondition = new StorageMutationCondition { ExpectedETag = seen.ETag } });

        Assert.Equal(StorageErrors.ConflictCode, report.Error?.Code);
        Assert.False(report.DestinationCommitted);
        Assert.Null(report.BackupRestored);
        Assert.Empty(await ListAllAsync(local));
    }

    // ---------------------------------------------------------------- A11

    private static InterceptBackend CommitsThenReportsALeftover(LocalStorageBackend local, string leftover = ".cl-storage-backup-provider.tmp") => new(local)
    {
        Move = async (from, to, options, token) =>
        {
            var moved = await local.MoveAsync(from, to, options, token);
            if (moved.IsFailure || !from.Contains(".cl-storage-", StringComparison.Ordinal)) return moved;
            // The provider committed, then could not remove its own backup.
            return Result.Failure(StorageErrors.PartialFailure("The provider's backup could not be removed.",
                $"{StorageErrorInfo.DestinationStateKey}=complete;{StorageErrorInfo.LeftBehindKey}={leftover}"));
        }
    };

    [Fact] // needs-review A11
    public async Task A_provider_error_after_the_commit_is_a_committed_transfer_with_its_leftover()
    {
        var (library, directory, _, b) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var local = Local(b);
        Assert.True(library.RegisterBackend("H", CommitsThenReportsALeftover(local).Named("H")).IsSuccess);
        await library.GetStorage("Default").UploadBytesAsync("new.bin", [9, 9]);

        var copied = await library.CopyAsync("Default", "new.bin", "H", "t.bin");
        var resumed = await library.CopyAsync("Default", "new.bin", "H", "r.bin", new StorageTransferOptions { ConflictPolicy = StorageConflictPolicy.Resume });

        Assert.True(copied.IsSuccess, copied.Error?.ToString());
        Assert.True(copied.DestinationCommitted);
        Assert.Equal(".cl-storage-backup-provider.tmp", copied.BackupLeftBehind);
        Assert.True(resumed.IsSuccess, resumed.Error?.ToString());
        Assert.Null(resumed.ResumeToken);
        Assert.Equal([9, 9], (await local.DownloadBytesAsync("t.bin")).Value!);
    }

    [Fact] // needs-review A11, B18
    public async Task A_native_move_whose_source_stayed_or_that_stopped_part_way_needs_reconciliation()
    {
        var (library, directory, _, b) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var local = Local(b);
        // The provider copied, then could not delete the source: it names the source, which is not a leftover.
        Assert.True(library.RegisterBackend("Kept", new InterceptBackend(local)
        {
            Move = async (from, to, options, token) =>
            {
                var copied = await local.CopyAsync(from, to, (options ?? new()) with { SourceVersionId = null, ExpectedSourceETag = null }, token);
                return copied.IsFailure ? copied : Result.Failure(StorageErrors.PartialFailure("The source could not be deleted.",
                    $"sourceDeleteError=storage.permission_denied;{StorageErrorInfo.DestinationStateKey}=complete;{StorageErrorInfo.LeftBehindKey}={from}"));
            }
        }.Named("Kept")).IsSuccess);
        // A WebDAV collection move answered 207: some members moved, some did not.
        Assert.True(library.RegisterBackend("Partial", new InterceptBackend(local)
        {
            Move = (_, _, _, _) => Task.FromResult(Result.Failure(StorageErrors.PartialFailure("Multi-status.",
                $"{StorageErrorInfo.HttpStatusKey}=207;{StorageErrorInfo.DestinationStateKey}=partial")))
        }.Named("Partial")).IsSuccess);
        await local.UploadBytesAsync("a.bin", [1]);
        await local.UploadBytesAsync("c.bin", [2]);

        var kept = await library.MoveAsync("Kept", "a.bin", "Kept", "b.bin");
        var partial = await library.MoveAsync("Partial", "c.bin", "Partial", "d.bin");

        Assert.Equal(StorageTransferOutcome.NeedsReconciliation, kept.Outcome);
        Assert.True(kept.DestinationCommitted);
        Assert.False(kept.SourceDeleted);
        Assert.Null(kept.StagingLeftBehind);
        Assert.Null(kept.BackupLeftBehind);
        Assert.Equal(StorageTransferOutcome.NeedsReconciliation, partial.Outcome);
        Assert.Equal(StorageErrors.PartialFailureCode, partial.Error?.Code);
    }

    [Fact] // needs-review A11
    public async Task Staged_uploads_and_streamed_writes_treat_a_provider_error_after_the_commit_as_committed()
    {
        using var directory = new TestDirectory();
        var local = Local(directory.Path);
        var storage = CommitsThenReportsALeftover(local);

        var uploaded = await StorageTransferPipeline.StagedUploadAsync(storage, "u.bin", new MemoryStream([1, 2]), new StorageUploadOptions { Verify = true }, CancellationToken.None);
        var writer = (await storage.OpenWriteAsync("w.bin")).Value!;
        await writer.WriteAsync(new byte[] { 3, 4, 5 });
        var committed = await writer.CommitAsync();

        Assert.True(uploaded.IsSuccess, uploaded.Error?.ToString());
        Assert.True(committed.IsSuccess, committed.Error?.ToString());
        Assert.Equal([3, 4, 5], (await local.DownloadBytesAsync("w.bin")).Value!);
    }

    // ---------------------------------------------------------------- A12

    [Fact] // needs-review A12
    public async Task A_cancelled_write_aborts_the_stream_so_a_retry_cannot_duplicate_bytes()
    {
        using var directory = new TestDirectory();
        var local = Local(directory.Path);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new InterceptBackend(local)
        {
            Upload = async (path, source, options, token) =>
            {
                await release.Task.WaitAsync(token);
                return await local.UploadAsync(path, source, options, token);
            }
        };
        var writer = (await slow.OpenWriteAsync("w.bin")).Value!;
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        // More than the pipe buffers: the write waits for the destination, and the wait is cancelled.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.WriteAsync(new byte[2_000_000], cancel.Token).AsTask());
        release.TrySetResult();

        await Assert.ThrowsAnyAsync<ObjectDisposedException>(() => writer.WriteAsync(new byte[10]).AsTask());
        Assert.False((await writer.CommitAsync()).IsSuccess);
        await writer.DisposeAsync();
        Assert.False((await local.ExistsAsync("w.bin")).Value);
    }

    // ---------------------------------------------------------------- A13

    [Fact] // needs-review A13
    public async Task A_native_copy_that_succeeded_is_not_reported_cancelled_when_the_cancel_lands_after_it()
    {
        var (library, directory, _, _) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var local = Local(directory.CreateDirectory("n"));
        await local.UploadBytesAsync("a.bin", [1, 2, 3]);
        using var cancel = new CancellationTokenSource();
        var native = new InterceptBackend(local)
        {
            Copy = async (from, to, options, token) =>
            {
                var copied = await local.CopyAsync(from, to, options, token);
                // The caller cancels (or the library shuts down) just as the server-side copy finished.
                await cancel.CancelAsync();
                return copied;
            }
        };
        Assert.True(library.RegisterBackend("N", native.Named("N")).IsSuccess);

        var report = await library.CopyAsync("N", "a.bin", "N", "b.bin", cancellationToken: cancel.Token);

        Assert.Equal(StorageTransferOutcome.Completed, report.Outcome);
        Assert.True(report.DestinationCommitted);
        Assert.NotNull(report.DestinationETag);
        Assert.Equal([1, 2, 3], (await local.DownloadBytesAsync("b.bin")).Value!);
    }

    // ---------------------------------------------------------------- B1

    [Fact] // needs-review B1
    public async Task Moving_a_single_link_that_is_skipped_leaves_it_in_place()
    {
        var (library, directory, _, _) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var deletes = 0;
        var source = new FakeStorageBackend(
            "Src",
            getInfo: (path, _) => Task.FromResult(Result<StorageItem>.Success(new StorageItem { Path = path, Name = path, ItemType = StorageItemType.Link })),
            delete: (_, _) => { Interlocked.Increment(ref deletes); return Task.FromResult(Result.Success()); });
        Assert.True(library.RegisterBackend("Src", source).IsSuccess);

        var report = await library.MoveAsync("Src", "link", "B", "link", new StorageTransferOptions { LinkHandling = StorageLinkHandling.Skip });

        Assert.Equal(StorageTransferOutcome.Skipped, report.Outcome);
        Assert.False(report.SourceDeleted);
        Assert.Equal(1, report.SkippedFiles);
        Assert.Equal(0, deletes);
    }

    // ---------------------------------------------------------------- B2

    [Fact] // needs-review B2
    public async Task A_resume_into_a_folder_the_transfer_created_fails_cleanly_with_a_token()
    {
        var (library, directory, _, _) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var content = Content(200_000);
        Assert.True(library.RegisterBackend("Src", Source(content, (attempt, rest) => attempt == 1 ? new FailingStream(rest, failAfter: 120_000) : new MemoryStream(rest))).IsSuccess);
        var options = new StorageTransferOptions { ConflictPolicy = StorageConflictPolicy.Resume };

        var first = await library.CopyAsync("Src", "big.bin", "B", "new/sub/big.bin", options);
        var second = await library.CopyAsync("Src", "big.bin", "B", "new/sub/big.bin", options with { ResumeToken = first.ResumeToken });

        Assert.Equal(StorageTransferOutcome.Failed, first.Outcome);
        Assert.NotNull(first.ResumeToken);
        Assert.True(second.IsSuccess, second.Error?.ToString());
        Assert.True(second.BytesResumed > 0);
        Assert.Equal(content, (await library.GetStorage("B").DownloadBytesAsync("new/sub/big.bin")).Value!);
    }

    // ---------------------------------------------------------------- B4

    [Fact] // needs-review B4
    public async Task A_source_without_identity_never_gets_a_token_for_a_private_staging_object()
    {
        var (library, directory, _, b) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        using var cancel = new CancellationTokenSource();
        var content = Content(200_000);
        var source = Source(content, (_, rest) => new CancellingStream(rest, 100_000, cancel), eTag: null);
        Assert.True(library.RegisterBackend("Src", source).IsSuccess);
        var local = Local(b);
        var destination = new InterceptBackend(local)
        {
            // The staging object cannot be removed after the cancel.
            Delete = (path, options, token) => path.Contains(".cl-storage-transfer-", StringComparison.Ordinal)
                ? Task.FromResult(Result.Failure(StorageErrors.Unavailable("The server is briefly unavailable.")))
                : local.DeleteAsync(path, options, token)
        };
        Assert.True(library.RegisterBackend("D", destination.Named("D")).IsSuccess);

        var report = await library.CopyAsync("Src", "big.bin", "D", "big.bin", new StorageTransferOptions { ConflictPolicy = StorageConflictPolicy.Resume }, cancel.Token);

        Assert.Null(report.ResumeToken);
        Assert.NotNull(report.StagingLeftBehind);
        Assert.Equal(StorageTransferOutcome.NeedsReconciliation, report.Outcome);
    }

    // ---------------------------------------------------------------- B5

    [Fact] // needs-review B5
    public async Task ExpectedSha256_without_Verify_still_checks_the_destination_after_the_commit()
    {
        var (library, directory, _, b) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var local = Local(b);
        var hooked = new InterceptBackend(local)
        {
            Move = async (from, to, options, token) =>
            {
                var moved = await local.MoveAsync(from, to, options, token);
                if (to == "t.bin") await local.UploadBytesAsync("t.bin", [7, 7, 7]);
                return moved;
            }
        };
        Assert.True(library.RegisterBackend("H", hooked.Named("H")).IsSuccess);
        byte[] content = [9, 9];
        await library.GetStorage("Default").UploadBytesAsync("n.bin", content);

        var report = await library.CopyAsync("Default", "n.bin", "H", "t.bin",
            new StorageTransferOptions { ExpectedSha256 = Convert.ToHexStringLower(SHA256.HashData(content)) });

        Assert.Equal(StorageTransferOutcome.NeedsReconciliation, report.Outcome);
        Assert.True(report.DestinationCommitted);
    }

    // ---------------------------------------------------------------- B6

    [Fact] // needs-review B6
    public async Task A_throwing_provider_leaves_no_staging_or_backup_and_is_reported()
    {
        var (library, directory, _, b) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var local = Local(b);
        await local.UploadBytesAsync("target.bin", [1]);
        var throwing = new InterceptBackend(local)
        {
            Move = (from, to, options, token) => from.Contains(".cl-storage-transfer-", StringComparison.Ordinal) && to == "target.bin"
                ? throw new InvalidOperationException("provider bug")
                : local.MoveAsync(from, to, options, token)
        };
        Assert.True(library.RegisterBackend("H", throwing.Named("H")).IsSuccess);
        await library.GetStorage("Default").UploadBytesAsync("new.bin", [9, 9]);

        var report = await library.CopyAsync("Default", "new.bin", "H", "target.bin");

        Assert.False(report.IsSuccess);
        Assert.False(report.DestinationCommitted);
        Assert.Equal([1], (await local.DownloadBytesAsync("target.bin")).Value!);
        Assert.Equal(["target.bin"], await ListAllAsync(local));
    }

    // ---------------------------------------------------------------- B7

    [Fact] // needs-review B7
    public async Task A_failed_promote_keeps_the_resumable_part_file_for_a_token()
    {
        var (library, directory, _, b) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var content = Content(150_000);
        var reads = 0L;
        Assert.True(library.RegisterBackend("Src", Source(content, (_, rest) => new CountingReadStream(rest, count => Interlocked.Add(ref reads, count)))).IsSuccess);
        var local = Local(b);
        var failOnce = true;
        var destination = new InterceptBackend(local)
        {
            Move = (from, to, options, token) =>
            {
                if (failOnce && from.Contains(StagedWriter.ResumablePrefix, StringComparison.Ordinal))
                {
                    failOnce = false;
                    return Task.FromResult(Result.Failure(StorageErrors.Unavailable("The server is briefly unavailable.")));
                }
                return local.MoveAsync(from, to, options, token);
            }
        };
        Assert.True(library.RegisterBackend("D", destination.Named("D")).IsSuccess);
        var options = new StorageTransferOptions { ConflictPolicy = StorageConflictPolicy.Resume };

        var first = await library.CopyAsync("Src", "big.bin", "D", "big.bin", options);
        var readFirst = Interlocked.Read(ref reads);
        var second = await library.CopyAsync("Src", "big.bin", "D", "big.bin", options with { ResumeToken = first.ResumeToken });

        Assert.Equal(StorageTransferOutcome.Failed, first.Outcome);
        Assert.Equal(content.Length, first.ResumeToken?.BytesStaged);
        Assert.True(second.IsSuccess, second.Error?.ToString());
        Assert.Equal(readFirst, Interlocked.Read(ref reads));
        Assert.Equal(content, (await local.DownloadBytesAsync("big.bin")).Value!);
    }

    // ---------------------------------------------------------------- B8

    [Fact] // needs-review B8
    public async Task Staged_uploads_and_streamed_writes_pass_their_condition_to_the_providers_move()
    {
        using var directory = new TestDirectory();
        var local = Local(directory.Path);
        await local.UploadBytesAsync("c.bin", [1]);
        var seen = (await local.GetInfoAsync("c.bin")).Value!;
        var conditions = new ConcurrentQueue<StorageMutationCondition?>();
        var recording = new InterceptBackend(local)
        {
            Move = (from, to, options, token) =>
            {
                conditions.Enqueue(options?.DestinationCondition);
                return local.MoveAsync(from, to, options, token);
            }
        };
        var condition = new StorageMutationCondition { ExpectedETag = seen.ETag };

        var uploaded = await StorageTransferPipeline.StagedUploadAsync(recording, "c.bin", new MemoryStream([2, 2]), new StorageUploadOptions { Condition = condition }, CancellationToken.None);
        var after = (await local.GetInfoAsync("c.bin")).Value!;
        var writer = (await recording.OpenWriteAsync("c.bin", new StorageUploadOptions { Condition = new StorageMutationCondition { ExpectedETag = after.ETag } })).Value!;
        await writer.WriteAsync(new byte[] { 3, 3, 3 });
        var committed = await writer.CommitAsync();

        Assert.True(uploaded.IsSuccess, uploaded.Error?.ToString());
        Assert.True(committed.IsSuccess, committed.Error?.ToString());
        // Local cannot enforce a condition in its move (storage.unsupported), so each promote offers it first and
        // then, having checked it just before, moves without it.
        Assert.Equal([seen.ETag, null, after.ETag, null], conditions.Select(c => c?.ExpectedETag));
    }

    // ---------------------------------------------------------------- B10

    [Fact] // needs-review B10
    public async Task A_conditional_policy_judges_a_pinned_version_by_its_own_size()
    {
        var (library, directory, _, _) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var versions = new[]
        {
            new StorageVersion { Path = "f.bin", VersionId = "v1", ETag = "\"e1\"", Size = 3, LastModified = DateTimeOffset.UnixEpoch },
            new StorageVersion { Path = "f.bin", VersionId = "v2", ETag = "\"e2\"", Size = 5, LastModified = DateTimeOffset.UnixEpoch.AddDays(1), IsLatest = true }
        };
        var source = new FakeStorageBackend(
            "Src",
            capabilities: new StorageCapabilities(new StorageCapabilities(true, true, true, true, true, true).Features | StorageFeature.Versioning),
            getInfo: (path, _) => Task.FromResult(Result<StorageItem>.Success(new StorageItem
            {
                Path = path, Name = path, ItemType = StorageItemType.File, Size = 5, ETag = "\"e2\"", VersionId = "v2"
            })),
            listVersions: (_, _, _) => Task.FromResult(Result<StorageVersionPage>.Success(new StorageVersionPage(versions, null))),
            downloadWithOptions: (_, options, _) => Task.FromResult(Result<Stream>.Success(
                options?.VersionId == "v1" ? new MemoryStream([1, 2, 3]) : new MemoryStream([5, 5, 5, 5, 5]))));
        Assert.True(library.RegisterBackend("Src", source).IsSuccess);
        await library.GetStorage("B").UploadBytesAsync("x.bin", [8, 8, 8]);

        var report = await library.CopyAsync("Src", "f.bin", "B", "x.bin",
            new StorageTransferOptions { SourceVersionId = "v1", ConflictPolicy = StorageConflictPolicy.OverwriteIfSizeDiffers });

        Assert.Equal(StorageTransferOutcome.Skipped, report.Outcome);
        Assert.Equal(StorageSkipReason.SameSize, report.SkipReason);
        Assert.Equal([8, 8, 8], (await library.GetStorage("B").DownloadBytesAsync("x.bin")).Value!);
    }

    // ---------------------------------------------------------------- B11

    [Theory] // needs-review B11
    [InlineData("docs/name (1).txt", 1, "docs/name (2).txt")]
    [InlineData("name (9)", 2, "name (11)")]
    [InlineData("file.", 1, "file. (1)")]
    [InlineData("report.pdf", 3, "report (3).pdf")]
    [InlineData(".hidden", 1, ".hidden (1)")]
    public void Rename_candidates_count_on_and_never_end_in_a_dot(string path, int attempt, string expected) =>
        Assert.Equal(expected, StorageConflictResolver.Candidate(path, attempt));

    [Fact] // needs-review B11
    public async Task Rename_takes_the_next_name_when_the_chosen_one_is_taken_before_the_commit_or_is_a_folder()
    {
        var (library, directory, _, b) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var local = Local(b);
        await local.UploadBytesAsync("f.txt", [1]);
        await local.CreateDirectoryAsync("d.txt");
        var raced = false;
        var racing = new InterceptBackend(local)
        {
            Move = async (from, to, options, token) =>
            {
                if (to == "f (1).txt" && !raced)
                {
                    raced = true;
                    await local.UploadBytesAsync("f (1).txt", [5]);
                }
                return await local.MoveAsync(from, to, options, token);
            }
        };
        Assert.True(library.RegisterBackend("H", racing.Named("H")).IsSuccess);
        await library.GetStorage("Default").UploadBytesAsync("f.txt", [9]);
        var rename = new StorageTransferOptions { ConflictPolicy = StorageConflictPolicy.Rename };

        var taken = await library.CopyAsync("Default", "f.txt", "H", "f.txt", rename);
        var folder = await library.CopyAsync("Default", "f.txt", "H", "d.txt", rename);

        Assert.True(taken.IsSuccess, taken.Error?.ToString());
        Assert.Equal("f (2).txt", taken.WrittenPath);
        Assert.Equal([5], (await local.DownloadBytesAsync("f (1).txt")).Value!);
        Assert.True(folder.IsSuccess, folder.Error?.ToString());
        Assert.Equal("d (1).txt", folder.WrittenPath);
    }

    // ---------------------------------------------------------------- B12

    [Fact] // needs-review B12
    public void Validation_refuses_a_condition_with_another_policy_and_empty_source_identities()
    {
        var condition = new StorageMutationCondition { ExpectedETag = "\"e\"" };
        Assert.True(new StorageUploadOptions { Condition = condition, ConflictPolicy = StorageConflictPolicy.Rename }.Validate().IsFailure);
        Assert.True(new StorageUploadOptions { Condition = condition, ConflictPolicy = StorageConflictPolicy.Skip }.Validate().IsFailure);
        Assert.True(new StorageUploadOptions { Condition = condition, ConflictPolicy = StorageConflictPolicy.Overwrite }.Validate().IsSuccess);
        Assert.True(new StorageTransferOptions { SourceVersionId = "" }.Validate().IsFailure);
        Assert.True(new StorageTransferOptions { ExpectedSourceETag = " " }.Validate().IsFailure);
        Assert.True(new StorageTransferOptions { ResumeToken = new StorageResumeToken { DestinationPath = "a", StagingPath = "b" }, ConflictPolicy = StorageConflictPolicy.Resume, Overwrite = false }.Validate().IsSuccess);
    }

    // ---------------------------------------------------------------- B13

    [Fact] // needs-review B13
    public async Task A_cancelled_directory_transfer_whose_rollback_failed_needs_reconciliation()
    {
        var (library, directory, _, b) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        using var cancel = new CancellationTokenSource();
        var local = Local(b);
        var hooked = new InterceptBackend(local)
        {
            Move = async (from, to, options, token) =>
            {
                var moved = await local.MoveAsync(from, to, options, token);
                if (to == "out/a.txt") await cancel.CancelAsync();
                return moved;
            },
            // The rollback cannot delete what was committed.
            Delete = (path, options, token) => path.Contains(".cl-storage-", StringComparison.Ordinal)
                ? local.DeleteAsync(path, options, token)
                : Task.FromResult(Result.Failure(StorageErrors.PermissionDenied("Deleting is not allowed.")))
        };
        Assert.True(library.RegisterBackend("H", hooked.Named("H")).IsSuccess);
        await library.GetStorage("Default").UploadBytesAsync("tree/a.txt", [1]);
        await library.GetStorage("Default").UploadBytesAsync("tree/b.txt", [2]);

        var report = await library.CopyAsync("Default", "tree", "H", "out", cancellationToken: cancel.Token);

        Assert.Equal(StorageTransferOutcome.NeedsReconciliation, report.Outcome);
        Assert.Equal(StorageErrors.CancelledCode, report.Error?.Code);
    }

    // ---------------------------------------------------------------- B14

    [Fact] // needs-review B14
    public async Task A_staged_upload_failure_keeps_the_providers_details()
    {
        using var directory = new TestDirectory();
        var local = Local(directory.Path);
        var busy = new InterceptBackend(local)
        {
            CapabilitiesMap = capabilities => new StorageCapabilities(capabilities.Features & ~StorageFeature.Append),
            Upload = async (path, source, options, token) =>
            {
                var stored = await local.UploadAsync(path, source, options, token);
                // The part file's lock marker is written as usual; the part file's upload is answered "busy".
                if (path.EndsWith(".lock", StringComparison.Ordinal)) return stored;
                return Result<StorageItem>.Failure(StorageErrors.ServerBusy("Slow down.", $"{StorageErrorInfo.RetryAfterKey}=5000"));
            }
        };

        var uploaded = await StorageTransferPipeline.StagedUploadAsync(busy, "u.bin", new MemoryStream(Content(1000)),
            new StorageUploadOptions { ConflictPolicy = StorageConflictPolicy.Resume, SourceIdentity = "u", SourceIdentityIsContentVersion = true }, CancellationToken.None);

        Assert.Equal(StorageErrors.ServerBusyCode, uploaded.Error?.Code);
        Assert.True(StorageErrorInfo.TryGetRetryAfter(uploaded.Error, out var delay));
        Assert.Equal(TimeSpan.FromSeconds(5), delay);
        Assert.Contains("stagingPath=", uploaded.Error!.Details, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- B15

    [Fact] // needs-review B15
    public async Task The_already_complete_check_reads_the_source_without_progress_or_speed_limits()
    {
        using var directory = new TestDirectory();
        var storage = Local(directory.Path);
        var content = Content(300_000);
        await storage.UploadBytesAsync("d.bin", content);
        var progress = new RecordingProgress();

        var uploaded = await storage.UploadAsync("d.bin", new MemoryStream(content),
            new StorageUploadOptions { ConflictPolicy = StorageConflictPolicy.Resume, SourceIdentity = "d", SourceIdentityIsContentVersion = true, Progress = progress });

        Assert.True(uploaded.IsSuccess, uploaded.Error?.ToString());
        Assert.Empty(progress.Reports);
    }

    // ---------------------------------------------------------------- B16

    [Fact] // needs-review B16
    public async Task Appending_to_a_part_file_respects_the_destinations_upload_limit()
    {
        var (library, directory, _, b) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var limited = Local(b, "Slow");
        StorageTransferPipeline.SetLimits(limited, uploadBytesPerSecond: 32_768, downloadBytesPerSecond: null);
        Assert.True(library.RegisterBackend("Slow", limited).IsSuccess);
        await library.GetStorage("Default").UploadBytesAsync("big.bin", Content(64 * 1024));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var report = await library.CopyAsync("Default", "big.bin", "Slow", "big.bin", new StorageTransferOptions { ConflictPolicy = StorageConflictPolicy.Resume });
        clock.Stop();

        Assert.True(report.IsSuccess, report.Error?.ToString());
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(1), $"64 KiB at 32 KiB/s took {clock.Elapsed}");
    }

    // ---------------------------------------------------------------- B17

    [Fact] // needs-review B17
    public async Task Overwriting_on_a_connection_without_server_side_copy_makes_no_client_side_backup()
    {
        var (library, directory, _, b) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var local = Local(b);
        await local.UploadBytesAsync("target.bin", [1]);
        var copies = new ConcurrentQueue<string>();
        var ftp = new InterceptBackend(local)
        {
            // Like FTP and SFTP: a copy goes through the client (a download and an upload).
            CapabilitiesMap = capabilities => new StorageCapabilities(capabilities.Features & ~StorageFeature.ServerSideCopy),
            Copy = (from, to, options, token) =>
            {
                copies.Enqueue(to);
                return local.CopyAsync(from, to, options, token);
            }
        };
        Assert.True(library.RegisterBackend("Ftp", ftp.Named("Ftp")).IsSuccess);
        await library.GetStorage("Default").UploadBytesAsync("new.bin", [9, 9]);

        var report = await library.CopyAsync("Default", "new.bin", "Ftp", "target.bin");

        Assert.True(report.IsSuccess, report.Error?.ToString());
        Assert.Empty(copies);
        Assert.Equal([9, 9], (await local.DownloadBytesAsync("target.bin")).Value!);
        Assert.Equal(["target.bin"], await ListAllAsync(local));
    }

    // ---------------------------------------------------------------- B20

    [Fact] // needs-review B20
    public async Task Discarding_metadata_is_never_left_to_a_native_copy()
    {
        var (library, directory, _, _) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var local = Local(directory.CreateDirectory("n"));
        await local.UploadBytesAsync("a.bin", [1]);
        var copies = new ConcurrentQueue<(string From, string To)>();
        var native = new InterceptBackend(local)
        {
            Copy = (from, to, options, token) =>
            {
                copies.Enqueue((from, to));
                return local.CopyAsync(from, to, options, token);
            }
        };
        Assert.True(library.RegisterBackend("N", native.Named("N")).IsSuccess);

        var report = await library.CopyAsync("N", "a.bin", "N", "b.bin", new StorageTransferOptions { MetadataPreservation = StorageMetadataPreservation.Discard });

        Assert.True(report.IsSuccess, report.Error?.ToString());
        Assert.Empty(copies);
        Assert.Equal([1], (await local.DownloadBytesAsync("b.bin")).Value!);
    }

    // ---------------------------------------------------------------- B24

    private static CancellationTokenSource LinkedCancellation(StorageWriteStream writer) =>
        (CancellationTokenSource)typeof(StorageWriteStream)
            .GetField("_cancel", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(writer)!;

    [Fact] // needs-review B24
    public async Task Disposing_a_committed_write_synchronously_releases_its_hold_on_the_callers_token()
    {
        using var directory = new TestDirectory();
        var storage = Local(directory.Path);
        using var caller = new CancellationTokenSource();
        var writer = (await storage.OpenWriteAsync("w.bin", cancellationToken: caller.Token)).Value!;
        await writer.WriteAsync(new byte[] { 1, 2, 3 });
        Assert.True((await writer.CommitAsync()).IsSuccess);

        writer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => LinkedCancellation(writer).Token);
    }

    [Fact] // needs-review B24
    public async Task Disposing_during_a_commit_lets_the_commit_finish()
    {
        using var directory = new TestDirectory();
        var local = Local(directory.Path);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new InterceptBackend(local)
        {
            Upload = async (path, source, options, token) =>
            {
                await release.Task.WaitAsync(token);
                return await local.UploadAsync(path, source, options, token);
            }
        };
        var writer = (await slow.OpenWriteAsync("w.bin")).Value!;
        await writer.WriteAsync(new byte[] { 1, 2, 3 });
        using var caller = new CancellationTokenSource();
        var commit = writer.CommitAsync(caller.Token);

        await writer.DisposeAsync();
        var cancelError = Record.Exception(caller.Cancel);
        release.TrySetResult();
        var finished = await Record.ExceptionAsync(() => commit);

        Assert.Null(cancelError);
        Assert.True(finished is null or OperationCanceledException, finished?.ToString());
        Assert.False((await local.ExistsAsync("w.bin")).Value);
    }

    // ---------------------------------------------------------------- B25

    [Fact] // needs-review B25
    public async Task Copy_and_move_report_a_cancel_and_an_unknown_connection_instead_of_throwing()
    {
        var (library, directory, _, _) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        await library.GetStorage("Default").UploadBytesAsync("a.bin", [1]);

        var cancelled = await library.CopyAsync("Default", "a.bin", "B", "a.bin", cancellationToken: new CancellationToken(canceled: true));
        var unknown = await library.MoveAsync("Nope", "a.bin", "B", "a.bin");

        Assert.Equal(StorageTransferOutcome.Cancelled, cancelled.Outcome);
        Assert.Equal(StorageErrors.CancelledCode, cancelled.Error?.Code);
        Assert.Equal(StorageTransferOutcome.Failed, unknown.Outcome);
        Assert.Equal(StorageErrors.NotFoundCode, unknown.Error?.Code);
    }

    [Fact] // needs-review B25
    public async Task A_connections_copy_throws_on_a_cancel_like_every_other_connection_call()
    {
        var (library, directory, _, _) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        using var cancel = new CancellationTokenSource();
        var local = Local(directory.CreateDirectory("n"));
        await local.UploadBytesAsync("a.bin", Content(10_000));
        var cancelling = new InterceptBackend(local)
        {
            Upload = async (path, source, options, token) =>
            {
                await cancel.CancelAsync();
                token.ThrowIfCancellationRequested();
                return await local.UploadAsync(path, source, options, token);
            }
        };
        Assert.True(library.RegisterBackend("N", cancelling.Named("N")).IsSuccess);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            library.GetStorage("N").CopyAsync("a.bin", "b.bin", new StorageTransferOptions { Verify = true }, cancel.Token));
    }

    // ---------------------------------------------------------------- B26

    [Fact] // needs-review B26
    public async Task A_move_that_committed_but_kept_its_source_is_announced()
    {
        var events = new RecordingEventBus();
        var (library, directory, _, _) = await TwoConnectionsAsync(events);
        using var _l = library; using var _d = directory;
        var local = Local(directory.CreateDirectory("s"));
        await local.UploadBytesAsync("f.bin", [1]);
        var sticky = new InterceptBackend(local)
        {
            Delete = (path, options, token) => Task.FromResult(Result.Failure(StorageErrors.PermissionDenied("Deleting is not allowed.")))
        };
        Assert.True(library.RegisterBackend("S", sticky.Named("S")).IsSuccess);

        var report = await library.MoveAsync("S", "f.bin", "B", "f.bin");

        Assert.Equal(StorageTransferOutcome.NeedsReconciliation, report.Outcome);
        Assert.Contains(events.Published, e => e is StorageCrossConnectionCopyCompletedEvent copied && copied.DestinationPath == "f.bin");
    }

    // ---------------------------------------------------------------- B71

    [Fact] // needs-review B71
    public async Task Runtime_only_settings_are_copied_when_the_library_is_created()
    {
        using var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.CreateDirectory("library"));
        var settings = new StorageConfig { Enabled = true };
        using var library = new global::CL.Storage.StorageLibrary(new StorageLibraryOptions { RuntimeOnly = true, Settings = settings });
        settings.Enabled = false;
        await library.OnConfigureAsync(context);
        await context.Configuration.LoadAllAsync();
        await library.OnInitializeAsync(context);
        await library.OnStartAsync(context);
        settings.Enabled = false;

        var health = await library.HealthCheckAsync();

        Assert.DoesNotContain("disabled", health.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- C

    [Fact] // needs-review C
    public async Task A_single_file_whose_failed_commit_removed_the_destination_reports_the_restore()
    {
        var (library, directory, _, b) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var local = Local(b);
        await local.UploadBytesAsync("target.bin", [1]);
        var losing = new InterceptBackend(local)
        {
            Move = async (from, to, options, token) =>
            {
                if (to != "target.bin") return await local.MoveAsync(from, to, options, token);
                // A provider whose replace removed the destination before it failed.
                await local.DeleteAsync("target.bin");
                return Result.Failure(StorageErrors.ConnectionLost("The connection dropped."));
            }
        };
        Assert.True(library.RegisterBackend("H", losing.Named("H")).IsSuccess);
        await library.GetStorage("Default").UploadBytesAsync("new.bin", [9, 9]);

        var report = await library.CopyAsync("Default", "new.bin", "H", "target.bin");

        Assert.Equal(StorageTransferOutcome.Failed, report.Outcome);
        Assert.True(report.BackupRestored);
        Assert.Equal([1], (await local.DownloadBytesAsync("target.bin")).Value!);
        Assert.Equal(["target.bin"], await ListAllAsync(local));
    }

    [Fact] // needs-review C
    public void A_hand_built_failed_report_converts_to_a_result_without_throwing()
    {
        var result = new StorageTransferReport { Outcome = StorageTransferOutcome.Failed }.ToResult();

        Assert.True(result.IsFailure);
        Assert.NotNull(result.Error);
    }

    [Fact] // needs-review C
    public async Task A_directory_copy_that_only_skipped_files_committed_nothing()
    {
        var (library, directory, _, _) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        await library.GetStorage("Default").UploadBytesAsync("tree/a.txt", [1]);
        await library.GetStorage("B").UploadBytesAsync("out/a.txt", [9]);

        var report = await library.CopyAsync("Default", "tree", "B", "out", new StorageTransferOptions { ConflictPolicy = StorageConflictPolicy.Skip });

        Assert.True(report.IsSuccess, report.Error?.ToString());
        Assert.Equal(1, report.SkippedFiles);
        Assert.False(report.DestinationCommitted);
    }

    [Fact] // needs-review C
    public async Task A_pinned_version_can_be_read_when_the_latest_version_is_a_delete_marker()
    {
        var (library, directory, _, _) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var versions = new[]
        {
            new StorageVersion { Path = "f.bin", VersionId = "v1", ETag = "\"e1\"", Size = 3, LastModified = DateTimeOffset.UnixEpoch },
            new StorageVersion { Path = "f.bin", VersionId = "v2", IsDeleteMarker = true, IsLatest = true }
        };
        var source = new FakeStorageBackend(
            "Src",
            capabilities: new StorageCapabilities(new StorageCapabilities(true, true, true, true, true, true).Features | StorageFeature.Versioning),
            getInfo: (_, _) => Task.FromResult(Result<StorageItem>.Failure(StorageErrors.NotFound("The latest version is a delete marker."))),
            listVersions: (_, _, _) => Task.FromResult(Result<StorageVersionPage>.Success(new StorageVersionPage(versions, null))),
            downloadWithOptions: (_, options, _) => Task.FromResult(options?.VersionId == "v1"
                ? Result<Stream>.Success(new MemoryStream([1, 2, 3]))
                : Result<Stream>.Failure(StorageErrors.NotFound("gone"))));
        Assert.True(library.RegisterBackend("Src", source).IsSuccess);

        var report = await library.CopyAsync("Src", "f.bin", "B", "restored.bin", new StorageTransferOptions { SourceVersionId = "v1" });

        Assert.True(report.IsSuccess, report.Error?.ToString());
        Assert.Equal([1, 2, 3], (await library.GetStorage("B").DownloadBytesAsync("restored.bin")).Value!);
    }
}

/// <summary>Passes every call to a backend, with hooks to replace any of them.</summary>
internal sealed class InterceptBackend(IStorageBackend inner) : IStorageBackend, IStorageAppendService, IStorageVersionService
{
    public Func<StorageCapabilities, StorageCapabilities>? CapabilitiesMap { get; init; }
    public Func<string, string, StorageTransferOptions?, CancellationToken, Task<Result>>? Move { get; init; }
    public Func<string, string, StorageTransferOptions?, CancellationToken, Task<Result>>? Copy { get; init; }
    public Func<string, StorageDeleteOptions?, CancellationToken, Task<Result>>? Delete { get; init; }
    public Func<string, CancellationToken, Task<Result<StorageItem>>>? GetInfo { get; init; }
    public Func<string, StorageDownloadOptions?, CancellationToken, Task<Result<Stream>>>? Download { get; init; }
    public Func<string, Stream, StorageUploadOptions?, CancellationToken, Task<Result<StorageItem>>>? Upload { get; init; }

    private string? _id;

    /// <summary>Takes the connection ID it is registered under.</summary>
    public InterceptBackend Named(string id)
    {
        _id = id;
        return this;
    }

    public string ConnectionId => _id ?? inner.ConnectionId;
    public StorageProvider Provider => inner.Provider;
    public string Root => inner.Root;
    public StorageCapabilities Capabilities => CapabilitiesMap?.Invoke(inner.Capabilities) ?? inner.Capabilities;
    public Task<Result> CheckHealthAsync(CancellationToken cancellationToken = default) => inner.CheckHealthAsync(cancellationToken);
    public bool TryGetNativeClient<TClient>([NotNullWhen(true)] out TClient? client) where TClient : class => inner.TryGetNativeClient(out client);
    public Task<Result<NativeConnectionLease<TClient>>> OpenNativeConnectionAsync<TClient>(CancellationToken cancellationToken = default) where TClient : class =>
        inner.OpenNativeConnectionAsync<TClient>(cancellationToken);
    public ValueTask DisposeAsync() => inner.DisposeAsync();
    public Task<Result<StorageItem>> GetInfoAsync(string path, CancellationToken cancellationToken = default) =>
        GetInfo?.Invoke(path, cancellationToken) ?? inner.GetInfoAsync(path, cancellationToken);
    public Task<Result<bool>> ExistsAsync(string path, CancellationToken cancellationToken = default) => inner.ExistsAsync(path, cancellationToken);
    public Task<Result<StoragePage>> ListAsync(string path, StorageListOptions? options = null, CancellationToken cancellationToken = default) => inner.ListAsync(path, options, cancellationToken);
    public Task<Result> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) => inner.CreateDirectoryAsync(path, cancellationToken);
    public Task<Result<StorageItem>> UploadAsync(string path, Stream source, StorageUploadOptions? options = null, CancellationToken cancellationToken = default) =>
        Upload?.Invoke(path, source, options, cancellationToken) ?? inner.UploadAsync(path, source, options, cancellationToken);
    public Task<Result<StorageItem>> UploadBytesAsync(string path, byte[] content, StorageUploadOptions? options = null, CancellationToken cancellationToken = default) =>
        UploadAsync(path, new MemoryStream(content), options, cancellationToken);
    public Task<Result<Stream>> DownloadAsync(string path, StorageDownloadOptions? options = null, CancellationToken cancellationToken = default) =>
        Download?.Invoke(path, options, cancellationToken) ?? inner.DownloadAsync(path, options, cancellationToken);
    public Task<Result<byte[]>> DownloadBytesAsync(string path, StorageDownloadOptions? options = null, CancellationToken cancellationToken = default) => inner.DownloadBytesAsync(path, options, cancellationToken);
    public Task<Result> DeleteAsync(string path, StorageDeleteOptions? options = null, CancellationToken cancellationToken = default) =>
        Delete?.Invoke(path, options, cancellationToken) ?? inner.DeleteAsync(path, options, cancellationToken);
    public Task<Result> CopyAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default) =>
        Copy?.Invoke(sourcePath, destinationPath, options, cancellationToken) ?? inner.CopyAsync(sourcePath, destinationPath, options, cancellationToken);
    public Task<Result> MoveAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default) =>
        Move?.Invoke(sourcePath, destinationPath, options, cancellationToken) ?? inner.MoveAsync(sourcePath, destinationPath, options, cancellationToken);
    public Task<Result<StorageVersionPage>> ListVersionsAsync(string path, StorageVersionListOptions? options = null, CancellationToken cancellationToken = default) =>
        inner is IStorageVersionService versions
            ? versions.ListVersionsAsync(path, options, cancellationToken)
            : Task.FromResult(Result<StorageVersionPage>.Failure(StorageErrors.Unsupported("no versions")));
    public Task<Result> DeleteVersionAsync(string path, string versionId, CancellationToken cancellationToken = default) =>
        inner is IStorageVersionService versions
            ? versions.DeleteVersionAsync(path, versionId, cancellationToken)
            : Task.FromResult(Result.Failure(StorageErrors.Unsupported("no versions")));
    public Task<Result<StorageItem>> AppendAsync(string path, Stream source, CancellationToken cancellationToken = default) =>
        inner is IStorageAppendService append
            ? append.AppendAsync(path, source, cancellationToken)
            : Task.FromResult(Result<StorageItem>.Failure(StorageErrors.Unsupported("no append")));
}

/// <summary>Records every event published.</summary>
internal sealed class RecordingEventBus : IEventBus
{
    public ConcurrentQueue<IEvent> Published { get; } = new();
    public void Publish<T>(T @event) where T : IEvent => Published.Enqueue(@event);
    public Task PublishAsync<T>(T @event) where T : IEvent
    {
        Published.Enqueue(@event);
        return Task.CompletedTask;
    }
    public IEventSubscription Subscribe<T>(Action<T> handler) where T : IEvent => throw new NotSupportedException();
    public IEventSubscription SubscribeAsync<T>(Func<T, Task> handler) where T : IEvent => throw new NotSupportedException();
}

/// <summary>Records progress reports synchronously.</summary>
internal sealed class RecordingProgress : IProgress<StorageTransferProgress>
{
    public ConcurrentQueue<StorageTransferProgress> Reports { get; } = new();
    public void Report(StorageTransferProgress value) => Reports.Enqueue(value);
}

/// <summary>Serves its data, then fails instead of reporting the end, as a connection dropped after the last byte would.</summary>
internal sealed class FailingAtEndStream(byte[] data) : MemoryStream(data)
{
    public override bool CanSeek => false;
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await base.ReadAsync(buffer[..Math.Min(buffer.Length, 8192)], cancellationToken);
        return read == 0 ? throw new IOException("The connection was reset.") : read;
    }
    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = base.Read(buffer, offset, Math.Min(count, 8192));
        return read == 0 ? throw new IOException("The connection was reset.") : read;
    }
}

/// <summary>Serves data up to a point, says so, and waits for a signal before serving the rest.</summary>
internal sealed class GatedStream(byte[] data, int gateAt, TaskCompletionSource atGate, Task open) : MemoryStream(data)
{
    public override bool CanSeek => false;
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (Position >= gateAt && !open.IsCompleted)
        {
            atGate.TrySetResult();
            await open.WaitAsync(cancellationToken);
        }
        var limit = Position < gateAt ? (int)Math.Min(buffer.Length, gateAt - Position) : buffer.Length;
        return await base.ReadAsync(buffer[..Math.Min(limit, 8192)], cancellationToken);
    }
}

/// <summary>Cancels a token once a given number of bytes has been read, then reports the cancellation.</summary>
internal sealed class CancellingStream(byte[] data, int cancelAfter, CancellationTokenSource cancellation) : MemoryStream(data)
{
    public override bool CanSeek => false;
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (Position >= cancelAfter)
        {
            await cancellation.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
        }
        return await base.ReadAsync(buffer[..Math.Min(buffer.Length, 8192)], cancellationToken);
    }
}

/// <summary>Counts the bytes a reader takes from it.</summary>
internal sealed class CountingReadStream(byte[] data, Action<int> counted) : MemoryStream(data)
{
    public override bool CanSeek => false;
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await base.ReadAsync(buffer, cancellationToken);
        counted(read);
        return read;
    }
    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = base.Read(buffer, offset, count);
        counted(read);
        return read;
    }
}
