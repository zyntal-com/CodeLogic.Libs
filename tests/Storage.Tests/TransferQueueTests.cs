using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Events;
using CL.Storage.Models;
using CL.Storage.Providers.Local;
using CL.Storage.Queue;
using CL.Storage.Registry;
using CodeLogic.Core.Events;
using CodeLogic.Core.Results;
using Xunit;

namespace Storage.Tests;

/// <summary>Covers the background transfer queue: limits, priorities, control, states, persistence, and retries.</summary>
public sealed class TransferQueueTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Queued_copies_all_complete_and_report_state_changes()
    {
        await using var fixture = await Fixture.CreateAsync();
        for (var i = 0; i < 6; i++) await fixture.Source.UploadBytesAsync($"f{i}.bin", [1, 2, 3]);
        await using var queue = await fixture.OpenAsync();
        var changes = new List<StorageTransferJob>();
        queue.JobChanged += job => { lock (changes) changes.Add(job); };

        for (var i = 0; i < 6; i++) await queue.EnqueueCopyAsync("Source", $"f{i}.bin", "Destination", $"copy/f{i}.bin");
        await queue.WaitForIdleAsync().WaitAsync(Wait);

        Assert.All(queue.Jobs, job => Assert.Equal(StorageTransferState.Completed, job.State));
        Assert.All(Enumerable.Range(0, 6), i => Assert.True(fixture.Destination.ExistsAsync($"copy/f{i}.bin").Result.Value));
        lock (changes) Assert.Contains(changes, job => job.State == StorageTransferState.Running);
        Assert.All(queue.Jobs, job => Assert.NotNull(job.LastReport));
    }

    // Three connections allow six jobs under the per-connection limit, so the global limit of four is what binds.
    [Fact]
    public async Task Concurrency_never_exceeds_the_configured_limits()
    {
        await using var fixture = await Fixture.CreateAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;
        var peak = 0;
        var perConnection = new Dictionary<string, int>();
        var perConnectionPeak = 0;
        for (var c = 0; c < 3; c++)
        {
            var id = $"C{c}";
            var backend = new FakeStorageBackend(
                id,
                capabilities: new StorageCapabilities(StorageFeature.FileCopy | StorageFeature.ServerSideCopy),
                getInfo: (path, _) => Task.FromResult(Result<StorageItem>.Success(new StorageItem { Path = path, Name = path, ItemType = StorageItemType.File, Size = 1 })),
                copy: async (_, _, token) =>
                {
                    lock (perConnection)
                    {
                        peak = Math.Max(peak, ++running);
                        perConnection[id] = perConnection.GetValueOrDefault(id) + 1;
                        perConnectionPeak = Math.Max(perConnectionPeak, perConnection[id]);
                    }
                    try { await gate.Task.WaitAsync(token); }
                    finally { lock (perConnection) { running--; perConnection[id]--; } }
                    return Result.Success();
                });
            Assert.True(fixture.Library.RegisterBackend(id, backend).IsSuccess);
        }
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { MaxConcurrentTransfers = 4, MaxTransfersPerConnection = 2 });

        for (var i = 0; i < 9; i++) await queue.EnqueueCopyAsync($"C{i % 3}", "a.bin", $"C{i % 3}", $"copy/{i}.bin");
        await Eventually(() => { lock (perConnection) return running == 4; });
        await Task.Delay(100);
        lock (perConnection) Assert.Equal((4, 4), (running, peak));
        gate.TrySetResult();
        await queue.WaitForIdleAsync().WaitAsync(Wait);

        lock (perConnection) Assert.Equal((4, 2), (peak, perConnectionPeak));
        Assert.All(queue.Jobs, job => Assert.Equal(StorageTransferState.Completed, job.State));
    }

    [Fact]
    public async Task Higher_priorities_start_first_and_order_can_be_changed()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Source.UploadBytesAsync("a.bin", [1]);
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { MaxConcurrentTransfers = 1, StartPaused = true });
        var starts = new List<string>();
        queue.JobChanged += job => { if (job.State == StorageTransferState.Running) lock (starts) starts.Add(job.Destination); };

        await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", "low.bin", priority: -5);
        var first = (await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", "first.bin")).Value!;
        var second = (await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", "second.bin")).Value!;
        var urgent = (await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", "urgent.bin")).Value!;
        Assert.True((await queue.SetPriorityAsync(urgent.Id, 100)).IsSuccess);
        Assert.True((await queue.MoveUpAsync(second.Id)).IsSuccess);
        queue.Resume();
        await queue.WaitForIdleAsync().WaitAsync(Wait);

        lock (starts)
            Assert.Equal(["Destination:urgent.bin", "Destination:second.bin", "Destination:first.bin", "Destination:low.bin"], starts);
    }

    [Fact]
    public async Task Cancelled_queued_job_never_runs_and_publishes_an_event()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Source.UploadBytesAsync("a.bin", [1]);
        var cancelled = new List<StorageTransferCancelledEvent>();
        using var subscription = fixture.Events.Subscribe<StorageTransferCancelledEvent>(value => { lock (cancelled) cancelled.Add(value); });
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { StartPaused = true });

        var job = (await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", "never.bin")).Value!;
        Assert.True((await queue.CancelAsync(job.Id)).IsSuccess);
        queue.Resume();
        await queue.WaitForIdleAsync().WaitAsync(Wait);

        Assert.Equal(StorageTransferState.Cancelled, Assert.Single(queue.Jobs).State);
        Assert.False((await fixture.Destination.ExistsAsync("never.bin")).Value);
        Assert.True((await queue.CancelAsync(job.Id)).IsFailure);
        await Eventually(() => { lock (cancelled) return cancelled.Count == 1; });
    }

    [Fact]
    public async Task Permanent_failures_can_be_retried_and_finished_jobs_cleared()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var queue = await fixture.OpenAsync();

        await queue.EnqueueCopyAsync("Source", "missing.bin", "Destination", "copy.bin");
        await queue.WaitForIdleAsync().WaitAsync(Wait);

        var failed = Assert.Single(queue.FailedJobs);
        Assert.Equal(StorageErrors.NotFoundCode, failed.Error?.Code);
        Assert.Equal(1, failed.Attempts);

        await fixture.Source.UploadBytesAsync("missing.bin", [7]);
        Assert.Equal(1, await queue.RetryFailedAsync());
        await queue.WaitForIdleAsync().WaitAsync(Wait);

        Assert.Equal(StorageTransferState.Completed, Assert.Single(queue.Jobs).State);
        Assert.Equal(1, await queue.ClearAsync());
        Assert.Empty(queue.Jobs);
    }

    [Fact]
    public async Task Transient_failures_back_off_honouring_retry_after_and_publish_retrying()
    {
        await using var fixture = await Fixture.CreateAsync();
        var calls = 0;
        var fake = new FakeStorageBackend(
            "Flaky",
            capabilities: new StorageCapabilities(StorageFeature.FileCopy | StorageFeature.ServerSideCopy),
            getInfo: (path, _) => Task.FromResult(Result<StorageItem>.Success(new StorageItem { Path = path, Name = path, ItemType = StorageItemType.File, Size = 1 })),
            copy: (_, _, _) => Task.FromResult(++calls == 1
                ? Result.Failure(StorageErrors.ServerBusy("busy", $"{StorageErrorInfo.RetryAfterKey}=300"))
                : Result.Success()));
        Assert.True(fixture.Library.RegisterBackend("Flaky", fake).IsSuccess);
        var retrying = new List<StorageTransferRetryingEvent>();
        using var subscription = fixture.Events.Subscribe<StorageTransferRetryingEvent>(value => { lock (retrying) retrying.Add(value); });
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { RetryBaseDelay = TimeSpan.FromMilliseconds(10) });

        var started = DateTimeOffset.UtcNow;
        await queue.EnqueueCopyAsync("Flaky", "a.bin", "Flaky", "b.bin");
        await queue.WaitForIdleAsync().WaitAsync(Wait);

        var job = Assert.Single(queue.Jobs);
        Assert.Equal((StorageTransferState.Completed, 2), (job.State, job.Attempts));
        Assert.True(DateTimeOffset.UtcNow - started >= TimeSpan.FromMilliseconds(290));
        await Eventually(() => { lock (retrying) return retrying.Count == 1; });
        lock (retrying) Assert.True(retrying[0].Delay >= TimeSpan.FromMilliseconds(290));
    }

    [Fact]
    public async Task Trust_and_credential_failures_block_instead_of_retrying()
    {
        await using var fixture = await Fixture.CreateAsync();
        var calls = 0;
        var fake = new FakeStorageBackend(
            "Locked",
            capabilities: new StorageCapabilities(StorageFeature.FileCopy | StorageFeature.ServerSideCopy),
            getInfo: (path, _) => Task.FromResult(Result<StorageItem>.Success(new StorageItem { Path = path, Name = path, ItemType = StorageItemType.File, Size = 1 })),
            copy: (_, _, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(Result.Failure(StorageErrors.AuthenticationFailed("refused")));
            });
        Assert.True(fixture.Library.RegisterBackend("Locked", fake).IsSuccess);
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { RetryBaseDelay = TimeSpan.Zero });

        await queue.EnqueueCopyAsync("Locked", "a.bin", "Locked", "b.bin");
        await queue.WaitForIdleAsync().WaitAsync(Wait);

        var job = Assert.Single(queue.Jobs);
        Assert.Equal((StorageTransferState.Blocked, StorageTransferBlockReason.Credential), (job.State, job.BlockReason));
        Assert.Equal(1, calls);
        Assert.Equal(StorageTransferBlockReason.Trust, StorageTransferQueue.BlockReasonFor(StorageErrors.HostKeyRejected("key")));
    }

    [Fact]
    public async Task A_move_whose_source_cannot_be_deleted_needs_reconciliation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var deletable = false;
        var fake = new FakeStorageBackend(
            "Sticky",
            getInfo: (path, _) => Task.FromResult(Result<StorageItem>.Success(new StorageItem { Path = path, Name = path, ItemType = StorageItemType.File, Size = 1 })),
            downloadWithOptions: (_, _, _) => Task.FromResult(Result<Stream>.Success(new MemoryStream([1]))),
            delete: (_, _) => Task.FromResult(Volatile.Read(ref deletable) ? Result.Success() : Result.Failure(StorageErrors.PermissionDenied("locked"))));
        Assert.True(fixture.Library.RegisterBackend("Sticky", fake).IsSuccess);
        await using var queue = await fixture.OpenAsync();

        await queue.EnqueueMoveAsync("Sticky", "a.bin", "Destination", "a.bin");
        await queue.WaitForIdleAsync().WaitAsync(Wait);

        var job = Assert.Single(queue.Jobs);
        Assert.Equal(StorageTransferState.NeedsReconciliation, job.State);
        Assert.True(job.LastReport!.DestinationCommitted);

        // The retried job runs, and with the source deletable now the move completes.
        Volatile.Write(ref deletable, true);
        Assert.True((await queue.RetryAsync(job.Id)).IsSuccess);
        await queue.WaitForIdleAsync().WaitAsync(Wait);
        var retried = Assert.Single(queue.Jobs);
        Assert.Equal(StorageTransferState.Completed, retried.State);
        Assert.True(retried.LastReport!.SourceDeleted);
    }

    [Fact]
    public async Task Job_ids_are_idempotent()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { StartPaused = true });

        var first = await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", "b.bin", jobId: "job-1");
        var again = await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", "b.bin", jobId: "job-1");
        var different = await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", "other.bin", jobId: "job-1");

        Assert.Equal("job-1", first.Value!.Id);
        Assert.Equal(first.Value.Id, again.Value!.Id);
        Assert.Equal(StorageErrors.ConflictCode, different.Error!.Code);
        Assert.Single(queue.Jobs);
    }

    [Fact]
    public async Task Single_jobs_can_be_paused_resumed_and_removed()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Source.UploadBytesAsync("a.bin", [1]);
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { StartPaused = true });
        var held = (await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", "held.bin")).Value!;
        var gone = (await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", "gone.bin")).Value!;
        await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", "runs.bin");

        Assert.True((await queue.PauseJobAsync(held.Id)).IsSuccess);
        Assert.True((await queue.RemoveAsync(gone.Id)).IsSuccess);
        queue.Resume();
        await queue.WaitForIdleAsync().WaitAsync(Wait);

        Assert.Equal(StorageTransferState.Paused, queue.Get(held.Id)!.State);
        Assert.Null(queue.Get(gone.Id));
        Assert.True((await fixture.Destination.ExistsAsync("runs.bin")).Value);
        Assert.False((await fixture.Destination.ExistsAsync("held.bin")).Value);

        Assert.True((await queue.ResumeJobAsync(held.Id)).IsSuccess);
        await queue.WaitForIdleAsync().WaitAsync(Wait);
        Assert.Equal(StorageTransferState.Completed, queue.Get(held.Id)!.State);
    }

    [Fact]
    public async Task Pausing_a_running_resumable_copy_continues_from_its_staged_bytes()
    {
        await using var fixture = await Fixture.CreateAsync();
        var content = Enumerable.Range(0, 600_000).Select(i => (byte)(i % 199)).ToArray();
        await fixture.Source.UploadBytesAsync("big.bin", content);
        StorageTransferPipeline.SetLimits(fixture.Source, uploadBytesPerSecond: null, downloadBytesPerSecond: 400_000);
        await using var queue = await fixture.OpenAsync();
        var job = (await queue.EnqueueCopyAsync("Source", "big.bin", "Destination", "big.bin",
            new StorageTransferOptions { ConflictPolicy = StorageConflictPolicy.Resume, Verify = true })).Value!;

        await Eventually(() => queue.Get(job.Id)?.Progress?.BytesTransferred > 100_000);
        Assert.True((await queue.PauseJobAsync(job.Id)).IsSuccess);
        await Eventually(() => queue.Get(job.Id)!.State == StorageTransferState.Paused);
        Assert.False((await fixture.Destination.ExistsAsync("big.bin")).Value);

        StorageTransferPipeline.SetLimits(fixture.Source, uploadBytesPerSecond: null, downloadBytesPerSecond: null);
        Assert.True((await queue.ResumeJobAsync(job.Id)).IsSuccess);
        await queue.WaitForIdleAsync().WaitAsync(Wait);

        var done = queue.Get(job.Id)!;
        Assert.Equal(StorageTransferState.Completed, done.State);
        Assert.True(done.LastReport!.BytesResumed > 0);
        Assert.Equal(content, (await fixture.Destination.DownloadBytesAsync("big.bin")).Value!);
    }

    [Fact]
    public async Task A_restart_requeues_jobs_that_never_touched_their_destination_and_interrupts_the_rest()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Source.UploadBytesAsync("a.bin", [1]);
        var store = new InMemoryStorageTransferJobStore();
        StorageTransferJobRecord Running(string id, StorageTransferPhase phase) => new()
        {
            Id = id,
            Spec = new StorageTransferJobSpec { Kind = StorageTransferKind.Copy, SourceConnectionId = "Source", SourcePath = "a.bin", DestinationConnectionId = "Destination", DestinationPath = $"{id}.bin" },
            State = StorageTransferState.Running,
            Checkpoint = new StorageTransferCheckpoint(phase),
            RetriesLeft = 3,
            EnqueuedAt = DateTimeOffset.UtcNow
        };
        // Left behind by a process that crashed; its lease has lapsed.
        await store.AddAsync(Running("staging", StorageTransferPhase.Transferring), default);
        await store.AddAsync(Running("committing", StorageTransferPhase.Committing), default);

        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store });
        await queue.WaitForIdleAsync().WaitAsync(Wait);

        Assert.Equal(StorageTransferState.Completed, queue.Get("staging")!.State);
        Assert.Equal(StorageTransferState.Interrupted, queue.Get("committing")!.State);
        Assert.False((await fixture.Destination.ExistsAsync("committing.bin")).Value);
    }

    [Fact]
    public async Task A_job_leased_by_another_worker_is_left_alone()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Source.UploadBytesAsync("a.bin", [1]);
        var store = new InMemoryStorageTransferJobStore();
        await using (var first = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store, StartPaused = true }))
            await first.EnqueueCopyAsync("Source", "a.bin", "Destination", "b.bin", jobId: "shared");
        var revision = (await store.GetAsync("shared", default))!.Revision;
        var lease = await store.TryClaimAsync("shared", "other-worker", revision, TimeSpan.FromMinutes(5), default);
        Assert.NotNull(lease);

        // No fixed delay; a job leased elsewhere leaves the queue idle without starting it.
        var starts = 0;
        await using var second = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store, StartPaused = true });
        second.JobChanged += job => { if (job.State == StorageTransferState.Running) Interlocked.Increment(ref starts); };
        second.Resume();
        await second.WaitForIdleAsync().WaitAsync(Wait);

        Assert.Equal(0, Volatile.Read(ref starts));
        Assert.Equal(StorageTransferState.Queued, second.Get("shared")!.State);
        Assert.False((await fixture.Destination.ExistsAsync("b.bin")).Value);
        Assert.Null(await store.TryClaimAsync("shared", "second", revision, TimeSpan.FromMinutes(1), default));
        // A worker that lost its lease cannot record an outcome.
        var stale = lease! with { FencingToken = lease.FencingToken - 1 };
        var record = (await store.GetAsync("shared", default))!;
        Assert.Null(await store.SaveAsync(record with { State = StorageTransferState.Completed }, stale, releaseLease: true, default));
        Assert.NotNull(await store.SaveAsync(record with { State = StorageTransferState.Completed }, lease, releaseLease: true, default));
    }

    [Fact]
    public async Task Progress_is_throttled_and_downloads_take_download_options()
    {
        await using var fixture = await Fixture.CreateAsync();
        var local = Path.Combine(fixture.Directory.Path, "local.bin");
        await File.WriteAllBytesAsync(local, [.. Enumerable.Range(0, 200_000).Select(i => (byte)i)]);
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { ProgressInterval = TimeSpan.FromSeconds(10) });
        var reports = new List<StorageTransferJob>();
        queue.ProgressChanged += job => { lock (reports) reports.Add(job); };

        await queue.EnqueueUploadAsync(local, "Destination", "up.bin");
        await queue.WaitForIdleAsync().WaitAsync(Wait);
        var down = Path.Combine(fixture.Directory.Path, "down.bin");
        await queue.EnqueueDownloadAsync("Destination", "up.bin", down, new StorageDownloadOptions { Offset = 100, Length = 50 });
        await queue.WaitForIdleAsync().WaitAsync(Wait);

        Assert.All(queue.Jobs, job => Assert.Equal(StorageTransferState.Completed, job.State));
        Assert.Equal(50, new FileInfo(down).Length);
        Assert.Equal((byte)100, (await File.ReadAllBytesAsync(down))[0]);
        // A long interval leaves at most the first and the final report of each job.
        lock (reports) Assert.InRange(reports.Count, 2, 4);
    }

    [Fact]
    public async Task Finished_history_is_capped()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Source.UploadBytesAsync("a.bin", [1]);
        var store = new InMemoryStorageTransferJobStore();
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store, MaxFinishedJobs = 2, MaxConcurrentTransfers = 1 });
        var removed = new List<string>();
        queue.JobRemoved += job => { lock (removed) removed.Add(job.Id); };

        for (var i = 0; i < 4; i++)
        {
            await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", $"{i}.bin", jobId: $"j{i}");
            await queue.WaitForIdleAsync().WaitAsync(Wait);
        }

        // The oldest two leave the store too, each announced once.
        await Eventually(() => { lock (removed) return removed.Count == 2; });
        Assert.Equal(["j2", "j3"], queue.Jobs.Select(job => job.Id).Order());
        Assert.Equal(["j2", "j3"], (await store.LoadAsync(default)).Select(record => record.Id).Order());
        lock (removed) Assert.Equal(["j0", "j1"], removed.Order());
    }

    [Fact]
    public async Task Adaptive_concurrency_starts_at_one_and_grows_with_success()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Source.UploadBytesAsync("a.bin", [1]);
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { AdaptiveConcurrency = true, MaxConcurrentTransfers = 4 });
        Assert.Equal(1, queue.ConcurrencyLimit);

        for (var i = 0; i < 3; i++) await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", $"{i}.bin");
        await queue.WaitForIdleAsync().WaitAsync(Wait);

        Assert.Equal(4, queue.ConcurrencyLimit);

        // A transient failure halves the limit.
        var busy = new FakeStorageBackend(
            "Busy",
            capabilities: new StorageCapabilities(StorageFeature.FileCopy | StorageFeature.ServerSideCopy),
            getInfo: (path, _) => Task.FromResult(Result<StorageItem>.Success(new StorageItem { Path = path, Name = path, ItemType = StorageItemType.File, Size = 1 })),
            copy: (_, _, _) => Task.FromResult(Result.Failure(StorageErrors.ServerBusy("busy"))));
        Assert.True(fixture.Library.RegisterBackend("Busy", busy).IsSuccess);
        var retrying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.JobChanged += job => { if (job.Id == "busy" && job.State == StorageTransferState.Queued && job.Attempts == 1) retrying.TrySetResult(); };
        await queue.EnqueueCopyAsync("Busy", "a.bin", "Busy", "b.bin", jobId: "busy");
        await retrying.Task.WaitAsync(Wait);
        Assert.Equal(2, queue.ConcurrencyLimit);
    }

    [Fact]
    public void Job_specs_round_trip_as_json()
    {
        var spec = new StorageTransferJobSpec
        {
            Kind = StorageTransferKind.Move,
            SourceConnectionId = "a",
            SourcePath = "x.bin",
            DestinationConnectionId = "b",
            DestinationPath = "y.bin",
            TransferOptions = new StorageTransferOptions
            {
                ConflictPolicy = StorageConflictPolicy.Resume,
                Verify = true,
                DestinationCondition = new StorageMutationCondition { ExpectedETag = "\"e\"" }
            }
        };

        var back = StorageTransferJobSpec.FromJson(spec.ToJson());

        Assert.True(back.SameWorkAs(spec));
        Assert.Equal("\"e\"", back.TransferOptions!.DestinationCondition!.ExpectedETag);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required TestDirectory Directory { get; init; }
        public required global::CL.Storage.StorageLibrary Library { get; init; }
        public required LocalStorageBackend Source { get; init; }
        public required LocalStorageBackend Destination { get; init; }
        public required EventBus Events { get; init; }

        public static async Task<Fixture> CreateAsync()
        {
            var directory = new TestDirectory();
            var events = new EventBus();
            var context = StorageLibraryTestSupport.CreateContext(directory.Path, events);
            var library = new global::CL.Storage.StorageLibrary();
            await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);
            var source = new LocalStorageBackend("Source", new LocalConnectionConfig { RootPath = directory.CreateDirectory("src") });
            var destination = new LocalStorageBackend("Destination", new LocalConnectionConfig { RootPath = directory.CreateDirectory("dst") });
            Assert.True(library.RegisterBackend("Source", source).IsSuccess);
            Assert.True(library.RegisterBackend("Destination", destination).IsSuccess);
            return new Fixture { Directory = directory, Library = library, Source = source, Destination = destination, Events = events };
        }

        public async Task<StorageTransferQueue> OpenAsync(StorageTransferQueueOptions? options = null)
        {
            var opened = await Library.OpenTransferQueueAsync(options);
            Assert.True(opened.IsSuccess, opened.Error?.ToString());
            return opened.Value!;
        }

        public ValueTask DisposeAsync()
        {
            Library.Dispose();
            Directory.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
