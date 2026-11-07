using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CodeLogic.Core.Results;

namespace CL.Storage.Sync;

public static partial class StorageSync
{

    /// <summary>Serializes applies of one sync within this process; an entry is dropped when no one holds or waits for it.</summary>
    private sealed class ApplyGate : IDisposable
    {
        private static readonly Dictionary<string, ApplyGate> Gates = [with(StringComparer.Ordinal)];
        private static readonly Lock GatesLock = new();
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private readonly string _syncId;
        private int _users;

        private ApplyGate(string syncId) => _syncId = syncId;

        public static async Task<ApplyGate> EnterAsync(string syncId, CancellationToken cancellationToken)
        {
            ApplyGate gate;
            lock (GatesLock)
            {
                if (!Gates.TryGetValue(syncId, out gate!)) Gates[syncId] = gate = new ApplyGate(syncId);
                gate._users++;
            }
            try
            {
                await gate._semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                gate.Leave();
                throw;
            }
            return gate;
        }

        public void Dispose()
        {
            _semaphore.Release();
            Leave();
        }

        private void Leave()
        {
            lock (GatesLock)
            {
                if (--_users == 0) Gates.Remove(_syncId);
            }
        }

        internal static bool IsHeld(string syncId)
        {
            lock (GatesLock) return Gates.ContainsKey(syncId);
        }
    }

    /// <summary>Whether a sync holds or waits for the in-process apply lock (for tests).</summary>
    internal static bool IsApplyGateHeld(string syncId) => ApplyGate.IsHeld(syncId);

    /// <summary>
    /// Plans and applies in one call. With <see cref="StorageSyncOptions.DryRun"/> the plan is returned and
    /// nothing changes. A plan with blocked conflicts is applied only with <see cref="StorageSyncOptions.ApplyWithConflicts"/>;
    /// otherwise the failure names them.
    /// </summary>
    /// <param name="source">Source connection.</param>
    /// <param name="sourcePath">Source directory.</param>
    /// <param name="destination">Destination connection.</param>
    /// <param name="destinationPath">Destination directory; created when missing.</param>
    /// <param name="options">Direction, conflicts, baseline, filters, and safety limits.</param>
    /// <param name="cancellationToken">
    /// Token used to cancel the sync. Cancelling while it plans throws <see cref="OperationCanceledException"/>;
    /// once it applies, the result is a success with <see cref="StorageSyncReport.Cancelled"/> set.
    /// </param>
    /// <returns>The steps taken, or planned for a dry run.</returns>
    public static async Task<Result<StorageSyncReport>> SyncAsync(
        this IStorageService source,
        string sourcePath,
        IStorageService destination,
        string destinationPath,
        StorageSyncOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new StorageSyncOptions();
        var plan = await source.PlanSyncAsync(sourcePath, destination, destinationPath, options, cancellationToken).ConfigureAwait(false);
        if (plan.IsFailure) return Result<StorageSyncReport>.Failure(plan.Error!);
        if (options.DryRun)
            return Result<StorageSyncReport>.Success(DryRunReport(plan.Value!));
        return await source.ApplySyncAsync(sourcePath, destination, destinationPath, plan.Value!, plan.Value!.Digest, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What a dry run reports: every step of the plan, not run (or withheld), and nothing saved.</summary>
    private static StorageSyncReport DryRunReport(StorageSyncPlan plan) => new()
    {
        Plan = plan,
        DryRun = true,
        Results = [.. plan.Actions.Select(action => new StorageSyncActionResult(action,
            action.WithheldReason is null ? StorageSyncActionOutcome.NotRun : StorageSyncActionOutcome.Withheld))]
    };

    private static async Task<Result<StorageSyncBaseline?>> LoadBaselineAsync(IStorageSyncStateStore store, string syncId, CancellationToken cancellationToken)
    {
        try
        {
            return Result<StorageSyncBaseline?>.Success(await store.LoadAsync(syncId, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Result<StorageSyncBaseline?>.Failure(StorageErrors.FromException(error, "Load the sync baseline"));
        }
    }

    // ---------------------------------------------------------------- planning

    /// <summary>
    /// What the plan leaves alone: paths left out on either side (filters, hidden items, skipped links, and
    /// everything below a folder left out), and paths a current filter would leave out (for baseline entries
    /// of items no longer listed). Their baseline entries are carried over, and nothing is deleted for them.
    /// </summary>
    private sealed class Scope(StorageDiff diff, PathFilter filter, StorageCompareOptions compare)
    {
        private readonly System.Text.RegularExpressions.Regex? _pattern =
            string.IsNullOrEmpty(compare.NamePattern) ? null : Providers.StorageListFilter.Glob(compare.NamePattern);

        /// <summary>Whether the path is left out on either side, or by the current filters.</summary>
        public bool LeftOut(string path, string destinationPath, bool isDirectory)
        {
            if (diff.SourceExclusions.Covers(path) || diff.DestinationExclusions.Covers(destinationPath)) return true;
            if (filter.Excludes(path, isDirectory)) return true;
            var name = path[(path.LastIndexOf('/') + 1)..];
            if (!compare.IncludeHidden && name.StartsWith('.')) return true;
            if (!isDirectory && _pattern is not null && !_pattern.IsMatch(name)) return true;
            // A folder above the path that the filters or the hidden rule leave out takes the path with it.
            for (var slash = path.IndexOf('/'); slash > 0; slash = path.IndexOf('/', slash + 1))
            {
                var folder = path[..slash];
                if (filter.Excludes(folder, isDirectory: true) || (!compare.IncludeHidden && folder[(folder.LastIndexOf('/') + 1)..].StartsWith('.')))
                    return true;
            }
            return false;
        }

        /// <summary>Whether the source left the path out, so the destination's copy must not be deleted.</summary>
        public bool LeftOutAtSource(string path) => diff.SourceExclusions.Covers(path);

        /// <summary>Whether the destination left the path out (a hidden item, a skipped link), so nothing is written there.</summary>
        public bool LeftOutAtDestination(string destinationPath) => diff.DestinationExclusions.Covers(destinationPath);

        /// <summary>
        /// Whether a path the current filters leave out lies in a folder the filters (or the hidden rule) leave out
        /// and that neither side has any more: its baseline entry describes files gone from both sides, so there is
        /// no deletion left to protect and the entry is dropped. A path left out by its own name, or in a folder that
        /// is still there on either side, keeps its entry, since whether the file is still there is not known.
        /// </summary>
        public bool GoneFromBothSides(string path, string destinationPath, bool isDirectory)
        {
            if (diff.SourceExclusions.Covers(path) || diff.DestinationExclusions.Covers(destinationPath)) return false;
            for (var end = path.IndexOf('/'); ; end = path.IndexOf('/', end + 1))
            {
                var last = end < 0;
                if (last && !isDirectory) return false;
                var folder = last ? path : path[..end];
                if (filter.Excludes(folder, isDirectory: true) || (!compare.IncludeHidden && folder[(folder.LastIndexOf('/') + 1)..].StartsWith('.')))
                    return !diff.SourceExclusions.Covers(folder) && !diff.DestinationExclusions.Covers(folder);
                if (last) return false;
            }
        }
    }

    private static (List<StorageSyncAction> Actions, int Unchanged) PlanOneWay(StorageDiff diff, StorageSyncOptions options, Scope scope, List<string> warnings)
    {
        var actions = new List<StorageSyncAction>();
        var unchanged = 0;
        var clashes = TypeClashes(diff);
        foreach (var entry in diff.Entries)
        {
            // Everything below a path that is a file on one side and a directory on the other is left alone with it.
            if (Under(clashes, entry.RelativePath, diff.CaseInsensitive)) continue;
            switch (entry.Kind)
            {
                case StorageDiffKind.Same:
                    if (!entry.IsDirectory) unchanged++;
                    break;
                case StorageDiffKind.OnlyInSource:
                    // What the destination left out for what it is there (a hidden item, a skipped link, anything
                    // below a skipped link to a folder) is not missing: nothing is written onto or through it.
                    if (scope.LeftOutAtDestination(entry.DestinationRelativePath ?? entry.RelativePath)) break;
                    actions.Add(entry.IsDirectory
                        ? new StorageSyncAction { RelativePath = entry.RelativePath, DestinationRelativePath = entry.DestinationRelativePath, Kind = StorageSyncActionKind.CreateDirectory }
                        : Copy(entry, StorageSyncActionKind.CopyToDestination));
                    break;
                case StorageDiffKind.OnlyInDestination when options.DeleteExtraneous:
                    // What the source left out for what it is (a link, an item hidden only there) is not
                    // extraneous: its copy stays. Files go one by one, each checked at apply time; a folder goes
                    // only once emptied, and never while it holds items the filters left out.
                    if (scope.LeftOutAtSource(entry.RelativePath)) break;
                    actions.Add(new StorageSyncAction
                    {
                        RelativePath = entry.RelativePath,
                        DestinationRelativePath = entry.DestinationRelativePath,
                        Kind = StorageSyncActionKind.DeleteFromDestination,
                        Destination = StorageSyncIdentity.Of(entry.Destination)
                    });
                    break;
                case StorageDiffKind.Different when entry.Reasons.HasFlag(StorageDiffReason.Type):
                    warnings.Add($"'{entry.RelativePath}' is a file on one side and a directory on the other; it and everything below it were left alone.");
                    break;
                case StorageDiffKind.Different when !entry.IsDirectory:
                    // Update and mirror never replace a newer destination with an older source.
                    if (entry.Reasons.HasFlag(StorageDiffReason.DestinationNewer))
                    {
                        if (entry.Reasons != StorageDiffReason.DestinationNewer)
                            warnings.Add($"'{entry.RelativePath}' is newer at the destination and differs; it was left alone.");
                        break;
                    }
                    // Content that could not be compared is left alone while size and time agree.
                    if (entry.Reasons == StorageDiffReason.Undecidable && SameSizeAndTime(entry, options.Compare.TimeTolerance))
                    {
                        warnings.Add($"'{entry.RelativePath}' could not be compared by content (hashing budget, or a file could not be read); size and time agree, so it was left alone.");
                        unchanged++;
                        break;
                    }
                    actions.Add(Copy(entry, StorageSyncActionKind.CopyToDestination));
                    break;
            }
        }
        KeepLinksWhole(actions, diff, null, null, warnings);
        DropUnsafeDirectoryDeletes(actions, diff, null, null);
        return (actions, unchanged);
    }

    private static bool SameSizeAndTime(StorageDiffEntry entry, TimeSpan tolerance) =>
        entry.Source?.Size == entry.Destination?.Size &&
        (StorageCompare.EffectiveModified(entry.Source!) is not { } a || StorageCompare.EffectiveModified(entry.Destination!) is not { } b || (a - b).Duration() <= tolerance);

    private static StorageSyncAction Copy(StorageDiffEntry entry, StorageSyncActionKind kind, StorageSyncConflictKind? conflict = null, string? path = null) => new()
    {
        RelativePath = path ?? entry.RelativePath,
        DestinationRelativePath = DestinationSpelling(entry, path ?? entry.RelativePath),
        Kind = kind,
        Reason = entry.Reasons,
        Bytes = kind == StorageSyncActionKind.CopyToDestination ? entry.Source?.Size : entry.Destination?.Size,
        Source = StorageSyncIdentity.Of(entry.Source),
        Destination = StorageSyncIdentity.Of(entry.Destination),
        Conflict = conflict
    };

    /// <summary>The destination's spelling of an entry, when it differs from the path a step names it by.</summary>
    private static string? DestinationSpelling(StorageDiffEntry? entry, string path)
    {
        var there = entry is null ? path : entry.DestinationRelativePath ?? entry.RelativePath;
        return string.Equals(there, path, StringComparison.Ordinal) ? null : there;
    }
}
