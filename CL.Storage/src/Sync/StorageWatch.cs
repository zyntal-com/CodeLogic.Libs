using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Sync;

/// <summary>What happened to an item.</summary>
public enum StorageChangeKind
{
    /// <summary>The item appeared.</summary>
    Created = 0,
    /// <summary>The item's content, size, or time changed.</summary>
    Changed = 1,
    /// <summary>The item disappeared.</summary>
    Deleted = 2,
    /// <summary>The item was renamed (native watching only; polling reports a delete and a create).</summary>
    Renamed = 3,
    /// <summary>
    /// Changes may have been missed (native watching only): notifications arrived faster than they could be
    /// buffered, or native watching stopped — the folder was removed or a network share dropped — and watching
    /// continues by polling. <see cref="StorageChange.Path"/> is the watched directory; list it again to catch up.
    /// </summary>
    Overflow = 4
}

/// <summary>One observed change.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Path">Storage path of the item after the change.</param>
/// <param name="OldPath">Previous path, for renames.</param>
/// <param name="ItemType">Item type, when known.</param>
/// <param name="ObservedAt">When the change was noticed.</param>
public sealed record StorageChange(StorageChangeKind Kind, string Path, string? OldPath, StorageItemType? ItemType, DateTimeOffset ObservedAt);

/// <summary>Controls watching a directory.</summary>
public sealed record StorageWatchOptions
{
    /// <summary>Gets whether subdirectories are watched too.</summary>
    public bool Recursive { get; init; } = true;
    /// <summary>Gets how often polling providers re-list the directory. Native watching ignores it.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Gets whether to poll even when the provider can notify natively.</summary>
    public bool ForcePolling { get; init; }
    /// <summary>
    /// Gets whether polls are incremental: a folder is listed again only when its modification time changed,
    /// which is when entries were added, removed, or renamed in it. Edits to a file's content, and changes
    /// deep inside a folder whose own time did not change, are caught by the full rescan every
    /// <see cref="FullRescanEvery"/> polls. Where folders have no times (object stores), listing them one by one
    /// would cost more than one recursive listing, so every poll is a full listing there.
    /// </summary>
    public bool Incremental { get; init; }
    /// <summary>Gets how many polls pass between full rescans in incremental mode.</summary>
    public int FullRescanEvery { get; init; } = 10;
    /// <summary>
    /// Gets an optional callback told about every poll that could not list the directory (the poll is retried on
    /// the next interval, and no changes are reported for it). It runs on the polling loop and must not block.
    /// </summary>
    public Action<Error>? PollFailed { get; init; }
}

/// <summary>Native change notifications, advertised with <see cref="StorageFeature.ChangeNotifications"/>.</summary>
public interface IStorageWatchService
{
    /// <summary>Streams changes under a directory until cancelled.</summary>
    /// <param name="path">Directory to watch.</param>
    /// <param name="recursive">Whether subdirectories are included.</param>
    /// <param name="cancellationToken">Stops watching.</param>
    /// <returns>Changes as they happen.</returns>
    IAsyncEnumerable<StorageChange> WatchNativeAsync(string path, bool recursive, CancellationToken cancellationToken);
}

/// <summary>Watches directories on any connection.</summary>
public static class StorageWatch
{
    /// <summary>
    /// Streams changes under a directory until cancelled: natively where the connection supports it
    /// (local folders), otherwise by listing on <see cref="StorageWatchOptions.PollInterval"/> and diffing.
    /// Listing failures while polling are skipped and retried on the next interval (see
    /// <see cref="StorageWatchOptions.PollFailed"/>); a failed first listing is retried before anything is reported,
    /// so it never makes every item look created.
    /// </summary>
    /// <param name="storage">Connection to watch.</param>
    /// <param name="path">Directory to watch.</param>
    /// <param name="options">Recursion and polling settings.</param>
    /// <param name="cancellationToken">Stops watching.</param>
    /// <returns>Changes as they are observed.</returns>
    public static IAsyncEnumerable<StorageChange> WatchAsync(
        this IStorageService storage,
        string path,
        StorageWatchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        options ??= new StorageWatchOptions();
        if (options.PollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "PollInterval must be positive.");
        if (options.FullRescanEvery < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "FullRescanEvery must be at least 1.");
        return !options.ForcePolling && storage is IStorageWatchService native && storage.Capabilities.Supports(StorageFeature.ChangeNotifications)
            ? WatchNativeThenPollAsync(storage, native, path, options, cancellationToken)
            : PollAsync(storage, path, options, cancellationToken);
    }

    /// <summary>
    /// Streams native notifications; if native watching fails (the folder was removed, a share dropped), reports
    /// <see cref="StorageChangeKind.Overflow"/> so the caller rescans, and goes on by polling.
    /// </summary>
    private static async IAsyncEnumerable<StorageChange> WatchNativeThenPollAsync(
        IStorageService storage,
        IStorageWatchService native,
        string path,
        StorageWatchOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var failed = false;
        IAsyncEnumerator<StorageChange>? changes = null;
        try
        {
            changes = native.WatchNativeAsync(path, options.Recursive, cancellationToken).GetAsyncEnumerator(cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Native watching could not even start (the folder is not there yet, the connection was replaced).
            failed = true;
        }
        try
        {
            while (changes is not null)
            {
                try
                {
                    if (!await changes.MoveNextAsync().ConfigureAwait(false)) break;
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    failed = true;
                    break;
                }
                yield return changes.Current;
            }
        }
        finally
        {
            if (changes is not null) await changes.DisposeAsync().ConfigureAwait(false);
        }
        if (!failed) yield break;
        // The polling baseline is taken before the caller is told to rescan, so nothing between the two is lost.
        var first = await SnapshotAsync(storage, path, options.Recursive, cancellationToken).ConfigureAwait(false);
        if (first.Error is not null) options.PollFailed?.Invoke(first.Error);
        var baseline = first.Items;
        var root = StoragePath.Normalize(path);
        yield return new StorageChange(StorageChangeKind.Overflow, root.IsSuccess ? root.Value! : path, null, StorageItemType.Directory, DateTimeOffset.UtcNow);
        await foreach (var change in PollAsync(storage, path, options, cancellationToken, baseline).ConfigureAwait(false))
            yield return change;
    }

    private static async IAsyncEnumerable<StorageChange> PollAsync(
        IStorageService storage,
        string path,
        StorageWatchOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        Dictionary<string, StorageItem>? baseline = null)
    {
        var previous = baseline;
        if (previous is null)
        {
            var first = await SnapshotAsync(storage, path, options.Recursive, cancellationToken).ConfigureAwait(false);
            if (first.Error is not null) options.PollFailed?.Invoke(first.Error);
            previous = first.Items;
        }
        using var timer = new PeriodicTimer(options.PollInterval);
        var polls = 0;
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (previous is null)
            {
                // No listing has worked yet: this one becomes the baseline, and nothing is reported as created.
                var retry = await SnapshotAsync(storage, path, options.Recursive, cancellationToken).ConfigureAwait(false);
                if (retry.Error is not null) options.PollFailed?.Invoke(retry.Error);
                previous = retry.Items;
                continue;
            }
            polls++;
            // Incremental polling only pays where folders carry their own modification times.
            var foldersHaveTimes = previous.Values.Any(item => item.ItemType == StorageItemType.Directory && item.LastModified is not null);
            var full = !options.Incremental || !options.Recursive || !foldersHaveTimes || polls % options.FullRescanEvery == 0;
            var snapshot = full
                ? await SnapshotAsync(storage, path, options.Recursive, cancellationToken).ConfigureAwait(false)
                : await IncrementalAsync(storage, path, previous, cancellationToken).ConfigureAwait(false);
            if (snapshot.Items is not { } current)
            {
                if (snapshot.Error is not null) options.PollFailed?.Invoke(snapshot.Error);
                continue;
            }
            var now = DateTimeOffset.UtcNow;
            foreach (var (key, item) in current)
            {
                if (!previous.TryGetValue(key, out var before))
                    yield return new StorageChange(StorageChangeKind.Created, key, null, item.ItemType, now);
                else if (item.ItemType == StorageItemType.File && (item.Size != before.Size || item.LastModified != before.LastModified || item.ETag != before.ETag))
                    yield return new StorageChange(StorageChangeKind.Changed, key, null, item.ItemType, now);
            }
            foreach (var (key, item) in previous)
            {
                if (!current.ContainsKey(key))
                    yield return new StorageChange(StorageChangeKind.Deleted, key, null, item.ItemType, now);
            }
            previous = current;
        }
    }

    private readonly record struct Snapshot(Dictionary<string, StorageItem>? Items, Error? Error);

    /// <summary>
    /// Lists the watched tree. A missing watched folder is empty; any other failure — including a folder that
    /// vanished part-way through the listing — gives no snapshot, so the poll is retried rather than reported as
    /// mass deletes (and, on the next poll, mass creates).
    /// </summary>
    private static async Task<Snapshot> SnapshotAsync(IStorageService storage, string path, bool recursive, CancellationToken cancellationToken)
    {
        var items = new Dictionary<string, StorageItem>(StringComparer.Ordinal);
        await foreach (var item in storage.EnumerateItemsAsync(path, new StorageListOptions { Recursive = recursive }, cancellationToken).ConfigureAwait(false))
        {
            if (item.IsFailure)
                return item.Error!.Code == StorageErrors.NotFoundCode && items.Count == 0 ? new Snapshot(items, null) : new Snapshot(null, item.Error);
            items[item.Value!.Path] = item.Value;
        }
        return new Snapshot(items, null);
    }

    private static async Task<Snapshot> IncrementalAsync(IStorageService storage, string root, IReadOnlyDictionary<string, StorageItem> previous, CancellationToken cancellationToken)
    {
        Error? failure = null;
        var items = await IncrementalSnapshotAsync(storage, root, previous, cancellationToken, error => failure = error).ConfigureAwait(false);
        return new Snapshot(items, failure);
    }

    /// <summary>
    /// Refreshes a snapshot by listing only folders whose modification time changed (and the root), reusing the
    /// previous snapshot for the rest. Returns null when a listing fails, so the poll is retried.
    /// </summary>
    internal static async Task<Dictionary<string, StorageItem>?> IncrementalSnapshotAsync(
        IStorageService storage,
        string root,
        IReadOnlyDictionary<string, StorageItem> previous,
        CancellationToken cancellationToken,
        Action<Error>? failed = null)
    {
        var normalizedRoot = StoragePath.Normalize(root);
        if (normalizedRoot.IsFailure) return null;
        var current = new Dictionary<string, StorageItem>(previous, StringComparer.Ordinal);
        // Children by folder, so replacing one folder's entries does not scan the whole snapshot.
        var byParent = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var key in current.Keys) Children(byParent, ParentOf(key)).Add(key);
        void Remove(string path)
        {
            current.Remove(path);
            if (byParent.Remove(path, out var nested))
            {
                foreach (var child in nested) Remove(child);
            }
        }

        var pending = new Queue<string>();
        pending.Enqueue(normalizedRoot.Value!);
        while (pending.Count > 0)
        {
            var directory = pending.Dequeue();
            var children = new Dictionary<string, StorageItem>(StringComparer.Ordinal);
            var vanished = false;
            await foreach (var item in storage.EnumerateItemsAsync(directory, new StorageListOptions(), cancellationToken).ConfigureAwait(false))
            {
                if (item.IsFailure)
                {
                    if (item.Error!.Code == StorageErrors.NotFoundCode) { vanished = true; break; }
                    failed?.Invoke(item.Error);
                    return null;
                }
                children[item.Value!.Path] = item.Value;
            }
            // A folder that vanished between polls goes with its whole subtree (the watched folder itself empties).
            if (vanished && directory != normalizedRoot.Value)
            {
                Remove(directory);
                byParent.GetValueOrDefault(ParentOf(directory))?.Remove(directory);
                continue;
            }
            // Replace this folder's direct children; a vanished child folder takes its whole subtree along.
            var siblings = Children(byParent, directory);
            foreach (var stale in siblings.Where(key => !children.ContainsKey(key)).ToList())
            {
                Remove(stale);
                siblings.Remove(stale);
            }
            foreach (var childPath in children.Keys) siblings.Add(childPath);
            foreach (var (childPath, child) in children)
            {
                current[childPath] = child;
                if (child.ItemType != StorageItemType.Directory) continue;
                var before = previous.GetValueOrDefault(childPath);
                // Descend where entries may have changed, or where the provider has no folder times at all.
                if (before is null || child.LastModified is null || before.LastModified != child.LastModified)
                    pending.Enqueue(childPath);
            }
        }
        return current;
    }

    private static HashSet<string> Children(Dictionary<string, HashSet<string>> byParent, string parent)
    {
        if (!byParent.TryGetValue(parent, out var children)) byParent[parent] = children = [with(StringComparer.Ordinal)];
        return children;
    }

    private static string ParentOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    /// <summary>Adapts <see cref="FileSystemWatcher"/> events to storage changes.</summary>
    internal static async IAsyncEnumerable<StorageChange> WatchFileSystemAsync(
        string fullPath,
        bool recursive,
        Func<string, string?> toStoragePath,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Bounded, so a flood of notifications cannot exhaust memory: what does not fit is reported as an overflow.
        var channel = Channel.CreateBounded<StorageChange>(new BoundedChannelOptions(WatchBufferCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        var overflowed = 0;
        using var watcher = new FileSystemWatcher(fullPath)
        {
            IncludeSubdirectories = recursive,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
            InternalBufferSize = 64 * 1024
        };
        void Post(StorageChangeKind kind, string path, string? oldPath)
        {
            if (toStoragePath(path) is not { } storagePath) return;
            var old = oldPath is null ? null : toStoragePath(oldPath);
            var internalOld = old is not null && Providers.StorageListFilter.IsInternal(old);
            if (Providers.StorageListFilter.IsInternal(storagePath))
            {
                // Renamed to a library-internal name (a backup made aside): to the caller the item disappeared.
                if (kind != StorageChangeKind.Renamed || old is null || internalOld) return;
                (kind, storagePath, old) = (StorageChangeKind.Deleted, old, null);
            }
            else if (internalOld)
            {
                // Renamed from a library-internal name (a staged write promoted): to the caller the item appeared.
                (kind, old) = (StorageChangeKind.Created, null);
            }
            StorageItemType? type = null;
            if (kind != StorageChangeKind.Deleted)
            {
                // One filesystem call per event on the watcher's thread.
                try { type = (File.GetAttributes(path) & FileAttributes.Directory) != 0 ? StorageItemType.Directory : StorageItemType.File; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            if (!channel.Writer.TryWrite(new StorageChange(kind, storagePath, old, type, DateTimeOffset.UtcNow)))
                Interlocked.Exchange(ref overflowed, 1);
        }
        watcher.Created += (_, e) => Post(StorageChangeKind.Created, e.FullPath, null);
        watcher.Changed += (_, e) => Post(StorageChangeKind.Changed, e.FullPath, null);
        watcher.Deleted += (_, e) => Post(StorageChangeKind.Deleted, e.FullPath, null);
        watcher.Renamed += (_, e) => Post(StorageChangeKind.Renamed, e.FullPath, e.OldFullPath);
        // A full buffer drops notifications silently unless the error is handled; report it so the caller rescans.
        // Any other error stops the watcher for good (the folder was removed, a share dropped): end the stream
        // with it, so the caller can fall back to polling.
        watcher.Error += (_, e) =>
        {
            if (e.GetException() is InternalBufferOverflowException)
            {
                if (!channel.Writer.TryWrite(new StorageChange(StorageChangeKind.Overflow, toStoragePath(fullPath) ?? string.Empty, null, StorageItemType.Directory, DateTimeOffset.UtcNow)))
                    Interlocked.Exchange(ref overflowed, 1);
            }
            else
                channel.Writer.TryComplete(e.GetException());
        };
        watcher.EnableRaisingEvents = true;
        var root = toStoragePath(fullPath) ?? string.Empty;
        await foreach (var change in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return change;
            if (Interlocked.Exchange(ref overflowed, 0) == 1)
                yield return new StorageChange(StorageChangeKind.Overflow, root, null, StorageItemType.Directory, DateTimeOffset.UtcNow);
        }
    }

    /// <summary>How many native notifications are buffered before the rest are reported as one overflow.</summary>
    internal const int WatchBufferCapacity = 8192;
}
