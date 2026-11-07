using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Events;
using CL.Storage.Models;
using CL.Storage.Providers;
using CL.Storage.Providers.Azure;
using CL.Storage.Providers.Ftp;
using CL.Storage.Providers.GoogleCloud;
using CL.Storage.Providers.Local;
using CL.Storage.Providers.S3;
using CL.Storage.Providers.Sftp;
using CL.Storage.Providers.Swift;
using CL.Storage.Providers.WebDav;
using CL.Storage.Registry;
using CodeLogic.Core.Configuration;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using CodeLogic.Framework.Libraries;

namespace CL.Storage;

public sealed partial class StorageLibrary
{

    private static Result<string> NormalizeTransferPath(string path, string role)
    {
        var normalized = StoragePath.Normalize(path);
        if (normalized.IsFailure)
            return normalized;
        return normalized.Value!.Length == 0
            ? Result<string>.Failure(StorageErrors.InvalidPath(
                $"A non-root transfer {role} path is required."))
            : normalized;
    }

    private static async Task<Result> PublishTransferResultAsync(
        Result result,
        StorageEventPublisher? publisher,
        bool sameBackend,
        bool move,
        string sourceConnectionId,
        StorageProvider sourceProvider,
        string sourcePath,
        string destinationConnectionId,
        StorageProvider destinationProvider,
        string destinationPath,
        StorageTransferSummary? summary)
    {
        if (result.IsFailure || publisher is null)
            return result;

        if (sameBackend)
        {
            if (move)
            {
                await publisher.PublishAsync(new StorageItemMovedEvent(
                    sourceConnectionId,
                    sourceProvider,
                    sourcePath,
                    destinationPath,
                    DateTimeOffset.UtcNow)).ConfigureAwait(false);
            }
            else
            {
                await publisher.PublishAsync(new StorageItemCopiedEvent(
                    sourceConnectionId,
                    sourceProvider,
                    sourcePath,
                    destinationPath,
                    DateTimeOffset.UtcNow)).ConfigureAwait(false);
            }
        }
        else if (move)
        {
            await publisher.PublishAsync(new StorageCrossConnectionMoveCompletedEvent(
                sourceConnectionId,
                sourceProvider,
                sourcePath,
                destinationConnectionId,
                destinationProvider,
                destinationPath,
                summary!.Files,
                summary.Directories,
                summary.Bytes,
                DateTimeOffset.UtcNow)).ConfigureAwait(false);
        }
        else
        {
            await publisher.PublishAsync(new StorageCrossConnectionCopyCompletedEvent(
                sourceConnectionId,
                sourceProvider,
                sourcePath,
                destinationConnectionId,
                destinationProvider,
                destinationPath,
                summary!.Files,
                summary.Directories,
                summary.Bytes,
                DateTimeOffset.UtcNow)).ConfigureAwait(false);
        }
        return result;
    }

    /// <summary>Compares two directory trees on (possibly different) connections.</summary>
    /// <param name="sourceConnectionId">Source connection.</param>
    /// <param name="sourcePath">Source directory.</param>
    /// <param name="destinationConnectionId">Destination connection.</param>
    /// <param name="destinationPath">Destination directory.</param>
    /// <param name="options">Comparison criteria.</param>
    /// <param name="cancellationToken">Token used to cancel the comparison.</param>
    /// <returns>Every path on either side with its relation.</returns>
    public Task<Result<Sync.StorageDiff>> CompareAsync(
        string sourceConnectionId,
        string sourcePath,
        string destinationConnectionId,
        string destinationPath,
        Sync.StorageCompareOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Sync.StorageSync.CompareAsync(GetStorage(sourceConnectionId), sourcePath, GetStorage(destinationConnectionId), destinationPath, options, cancellationToken);

    /// <summary>Synchronizes two directory trees on (possibly different) connections.</summary>
    /// <param name="sourceConnectionId">Source connection.</param>
    /// <param name="sourcePath">Source directory.</param>
    /// <param name="destinationConnectionId">Destination connection.</param>
    /// <param name="destinationPath">Destination directory.</param>
    /// <param name="options">Direction, deletes, dry run, and comparison settings.</param>
    /// <param name="cancellationToken">Token used to cancel the sync.</param>
    /// <returns>The steps taken, or planned for a dry run.</returns>
    public Task<Result<Sync.StorageSyncReport>> SyncAsync(
        string sourceConnectionId,
        string sourcePath,
        string destinationConnectionId,
        string destinationPath,
        Sync.StorageSyncOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Sync.StorageSync.SyncAsync(GetStorage(sourceConnectionId), sourcePath, GetStorage(destinationConnectionId), destinationPath, options, cancellationToken);

    /// <summary>Plans a sync between two connections without changing anything; approve the plan by its digest.</summary>
    /// <param name="sourceConnectionId">Source connection.</param>
    /// <param name="sourcePath">Source directory.</param>
    /// <param name="destinationConnectionId">Destination connection.</param>
    /// <param name="destinationPath">Destination directory.</param>
    /// <param name="options">Direction, conflicts, baseline, filters, and safety limits.</param>
    /// <param name="cancellationToken">Token used to cancel the listings.</param>
    /// <returns>The plan.</returns>
    public Task<Result<Sync.StorageSyncPlan>> PlanSyncAsync(
        string sourceConnectionId,
        string sourcePath,
        string destinationConnectionId,
        string destinationPath,
        Sync.StorageSyncOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Sync.StorageSync.PlanSyncAsync(GetStorage(sourceConnectionId), sourcePath, GetStorage(destinationConnectionId), destinationPath, options, cancellationToken);

    /// <summary>Applies an approved sync plan between two connections.</summary>
    /// <param name="sourceConnectionId">Source connection.</param>
    /// <param name="sourcePath">Source directory; must be the plan's.</param>
    /// <param name="destinationConnectionId">Destination connection.</param>
    /// <param name="destinationPath">Destination directory; must be the plan's.</param>
    /// <param name="plan">The plan.</param>
    /// <param name="approvedDigest">The digest that was approved.</param>
    /// <param name="options">The options the plan was made with.</param>
    /// <param name="cancellationToken">Stops the run; the report keeps what was done.</param>
    /// <returns>Each step's outcome.</returns>
    public Task<Result<Sync.StorageSyncReport>> ApplySyncAsync(
        string sourceConnectionId,
        string sourcePath,
        string destinationConnectionId,
        string destinationPath,
        Sync.StorageSyncPlan plan,
        string approvedDigest,
        Sync.StorageSyncOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Sync.StorageSync.ApplySyncAsync(GetStorage(sourceConnectionId), sourcePath, GetStorage(destinationConnectionId), destinationPath, plan, approvedDigest, options, cancellationToken);

    /// <summary>
    /// Opens a background transfer queue. Its jobs live in <see cref="Queue.StorageTransferQueueOptions.Store"/>
    /// (in memory by default); a durable store brings back the jobs of an earlier run, with those left
    /// running either queued again (when their destination was never touched) or marked interrupted.
    /// Stopping the library disposes the queue first, so its running jobs stop the same way.
    /// </summary>
    /// <param name="options">Limits, retries, and the job store.</param>
    /// <param name="cancellationToken">Token used to cancel loading the store.</param>
    /// <returns>The queue, or why the options are invalid.</returns>
    public async Task<Result<Queue.StorageTransferQueue>> OpenTransferQueueAsync(
        Queue.StorageTransferQueueOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureOperational();
        var opened = await Queue.StorageTransferQueue.OpenAsync(this, options ?? new Queue.StorageTransferQueueOptions(), cancellationToken).ConfigureAwait(false);
        if (opened.IsSuccess && !TrackQueue(opened.Value!))
        {
            // The library began stopping while the queue opened, after it had stopped the queues it knew of.
            await opened.Value!.DisposeAsync().ConfigureAwait(false);
            return Result<Queue.StorageTransferQueue>.Failure(StorageErrors.Unavailable("The storage library stopped while the transfer queue was opening."));
        }
        return opened;
    }

    /// <summary>Keeps a queue to stop with the library; false when the library is already stopping.</summary>
    private bool TrackQueue(Queue.StorageTransferQueue queue)
    {
        lock (_stateGate)
        {
            if (_state is LifecycleState.Stopping or LifecycleState.Stopped or LifecycleState.Disposed) return false;
            lock (_queues) _queues.Add(queue);
            return true;
        }
    }

    /// <summary>Forgets a queue that was disposed.</summary>
    internal void UntrackQueue(Queue.StorageTransferQueue queue)
    {
        lock (_queues) _queues.Remove(queue);
    }

    /// <summary>Returns an immutable snapshot containing sanitized connection information.</summary>
    public IReadOnlyList<StorageConnectionInfo> GetConnections()
    {
        EnsureOperational();
        lock (_registryGate)
        {
            var snapshot = _connectionInfos.Values
                .Select(connection => _health.TryGetValue(connection.Id, out var health) ? connection with { LastHealth = health } : connection)
                .OrderBy(connection => connection.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new ReadOnlyCollection<StorageConnectionInfo>(snapshot);
        }
    }

    /// <summary>
    /// Returns a reusable native client owned by the effective backend. A client already returned by this
    /// method becomes invalid when its connection is replaced or removed and must not be disposed by callers.
    /// </summary>
    public TClient GetNativeClient<TClient>(string connectionId = "Default") where TClient : class
    {
        ValidateConnectionId(connectionId);
        using var lease = AcquireOperation(connectionId);
        if (lease.Backend.TryGetNativeClient<TClient>(out var client))
            return client;
        throw new InvalidOperationException(
            $"Storage connection '{connectionId}' does not expose a reusable native client of type '{typeof(TClient).FullName}'.");
    }
}
