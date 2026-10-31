using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CodeLogic.Core.Results;

namespace CL.Storage.Sync;

/// <summary>
/// Synchronizes directory trees across any two storage connections, in two steps: a plan that can be shown,
/// stored, and approved by its digest, then an apply that re-checks every item against the plan. Two-way syncs
/// with a baseline are three-way: edits and deletions made on one side are carried over, and changes on both
/// sides are conflicts resolved by <see cref="StorageSyncOptions.ConflictPolicy"/>.
/// </summary>
public static class StorageSync
{
    /// <summary>Compares two directory trees recursively.</summary>
    /// <param name="source">Source connection.</param>
    /// <param name="sourcePath">Source directory.</param>
    /// <param name="destination">Destination connection.</param>
    /// <param name="destinationPath">Destination directory; a missing one compares as empty.</param>
    /// <param name="options">Comparison criteria, filters, and budgets.</param>
    /// <param name="cancellationToken">Token used to cancel the listings and checksums.</param>
    /// <returns>Every path on either side with its relation.</returns>
    public static Task<Result<StorageDiff>> CompareAsync(
        this IStorageService source,
        string sourcePath,
        IStorageService destination,
        string destinationPath,
        StorageCompareOptions? options = null,
        CancellationToken cancellationToken = default) =>
        StorageCompare.CompareAsync(source, sourcePath, destination, destinationPath, options, cancellationToken);

    /// <summary>
    /// Plans a sync without changing anything. The plan lists every step with the versions it depends on,
    /// withholds deletions a safety rule forbids (see <see cref="StorageSyncPlan.Warnings"/>), and carries a
    /// digest; pass that digest to <see cref="ApplySyncAsync"/> to run exactly this plan.
    /// </summary>
    /// <param name="source">Source connection.</param>
    /// <param name="sourcePath">Source directory.</param>
    /// <param name="destination">Destination connection.</param>
    /// <param name="destinationPath">Destination directory; created when missing.</param>
    /// <param name="options">Direction, conflicts, baseline, filters, and safety limits.</param>
    /// <param name="cancellationToken">Token used to cancel the listings.</param>
    /// <returns>The plan.</returns>
    public static async Task<Result<StorageSyncPlan>> PlanSyncAsync(
        this IStorageService source,
        string sourcePath,
        IStorageService destination,
        string destinationPath,
        StorageSyncOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        options ??= new StorageSyncOptions();
        var valid = options.Validate();
        if (valid.IsFailure) return Result<StorageSyncPlan>.Failure(valid.Error!);
        var sourceRoot = StoragePath.Normalize(sourcePath);
        if (sourceRoot.IsFailure) return Result<StorageSyncPlan>.Failure(sourceRoot.Error!);
        var destinationRoot = StoragePath.Normalize(destinationPath);
        if (destinationRoot.IsFailure) return Result<StorageSyncPlan>.Failure(destinationRoot.Error!);

        try
        {
            return await PlanCoreAsync(source, sourceRoot.Value!, destination, destinationRoot.Value!, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A filter pattern that runs too long, or a provider whose listing throws, fails the plan; only the
            // caller's cancellation is thrown.
            return Result<StorageSyncPlan>.Failure(StorageCompare.PlanningError(error, "Plan the sync"));
        }
    }

    private static async Task<Result<StorageSyncPlan>> PlanCoreAsync(
        IStorageService source,
        string sourceRoot,
        IStorageService destination,
        string destinationRoot,
        StorageSyncOptions options,
        CancellationToken cancellationToken)
    {
        var baseline = StorageSyncBaseline.Empty;
        if (options.StateStore is { } store)
        {
            var loaded = await LoadBaselineAsync(store, options.SyncId!, cancellationToken).ConfigureAwait(false);
            if (loaded.IsFailure) return Result<StorageSyncPlan>.Failure(loaded.Error!);
            baseline = loaded.Value ?? StorageSyncBaseline.Empty;
        }

        var filter = new PathFilter(options.Compare);
        var sourceTree = await StorageCompare.ListTreeAsync(source, sourceRoot, options.Compare, filter, required: true, cancellationToken).ConfigureAwait(false);
        if (sourceTree.IsFailure) return Result<StorageSyncPlan>.Failure(sourceTree.Error!);
        var destinationTree = await StorageCompare.ListTreeAsync(destination, destinationRoot, options.Compare, filter, required: false, cancellationToken).ConfigureAwait(false);
        if (destinationTree.IsFailure) return Result<StorageSyncPlan>.Failure(destinationTree.Error!);
        var diff = await StorageCompare.DiffAsync(source, sourceTree.Value!, destination, destinationTree.Value!, options.Compare, cancellationToken).ConfigureAwait(false);
        if (diff.IsFailure) return Result<StorageSyncPlan>.Failure(diff.Error!);

        var scope = new Scope(diff.Value!, filter, options.Compare);
        var warnings = new List<string>();
        List<StorageSyncAction> actions;
        int unchanged;
        var agreed = new Dictionary<string, StorageSyncBaselineEntry>(StringComparer.Ordinal);
        if (options.Direction == StorageSyncDirection.TwoWay)
            (actions, unchanged, agreed) = PlanTwoWay(diff.Value!, baseline, options, scope, warnings);
        else
            (actions, unchanged) = PlanOneWay(diff.Value!, options, scope, warnings);
        actions = ApplyDeletionSafety(actions, baseline, sourceTree.Value!, destinationTree.Value!, options, warnings);
        // A missing destination folder is created first, so parallel copies do not race to create it.
        if (destinationTree.Value!.Missing && destinationRoot.Length > 0 && actions.Count > 0)
            actions.Insert(0, new StorageSyncAction { RelativePath = string.Empty, Kind = StorageSyncActionKind.CreateDirectory });

        var plan = new StorageSyncPlan
        {
            SchemaVersion = StorageSyncPlan.CurrentSchemaVersion,
            SourceConnectionId = source.ConnectionId,
            DestinationConnectionId = destination.ConnectionId,
            SourceRoot = sourceRoot,
            DestinationRoot = destinationRoot,
            SyncId = options.SyncId,
            BaselineGeneration = baseline.Generation,
            Direction = options.Direction,
            ConflictPolicy = options.ConflictPolicy,
            CaseInsensitive = diff.Value!.CaseInsensitive,
            OptionsDigest = options.Fingerprint(),
            Actions = Order(ReadThroughLinks(actions, diff.Value!)),
            Unchanged = unchanged,
            Agreed = options.StateStore is not null ? agreed : [],
            Warnings = warnings,
            CreatedAt = DateTimeOffset.UtcNow
        };
        return Result<StorageSyncPlan>.Success(plan with { Digest = plan.ComputeDigest() });
    }

    /// <summary>
    /// Applies an approved plan. It is refused when its digest differs from <paramref name="approvedDigest"/>
    /// (it was changed after approval), when it was made for other connections, directories, or options, when its
    /// baseline moved on (another run synced in between), or when it has blocked conflicts (unless
    /// <see cref="StorageSyncOptions.ApplyWithConflicts"/>). Every step first checks that its items are still the
    /// versions planned; a changed item's step is reported <see cref="StorageSyncActionOutcome.Stale"/> and not taken.
    /// With <see cref="StorageSyncOptions.DryRun"/> the same checks run, and the report lists every step as not run:
    /// nothing is written and the baseline is not saved.
    /// </summary>
    /// <param name="source">Source connection.</param>
    /// <param name="sourcePath">Source directory; must be the plan's.</param>
    /// <param name="destination">Destination connection.</param>
    /// <param name="destinationPath">Destination directory; must be the plan's.</param>
    /// <param name="plan">The plan from <see cref="PlanSyncAsync"/>.</param>
    /// <param name="approvedDigest">The digest that was approved.</param>
    /// <param name="options">The options the plan was made with (store, verification, retries, concurrency).</param>
    /// <param name="cancellationToken">
    /// Stops the run. A run stopped once it started applying is not an exception: the result is a success whose
    /// report has <see cref="StorageSyncReport.Cancelled"/> set and says what was done before.
    /// </param>
    /// <returns>Each step's outcome.</returns>
    public static async Task<Result<StorageSyncReport>> ApplySyncAsync(
        this IStorageService source,
        string sourcePath,
        IStorageService destination,
        string destinationPath,
        StorageSyncPlan plan,
        string approvedDigest,
        StorageSyncOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(plan);
        options ??= new StorageSyncOptions();
        var valid = options.Validate();
        if (valid.IsFailure) return Result<StorageSyncReport>.Failure(valid.Error!);
        if (!string.Equals(plan.ComputeDigest(), plan.Digest, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(approvedDigest, plan.Digest, StringComparison.OrdinalIgnoreCase))
            return Result<StorageSyncReport>.Failure(StorageErrors.Conflict("The plan was changed after it was made, or a different plan was approved."));
        if (plan.SchemaVersion != StorageSyncPlan.CurrentSchemaVersion)
            return Result<StorageSyncReport>.Failure(StorageErrors.InvalidContent("The plan was made by a different version of the library; plan again."));
        if (StoragePath.Normalize(sourcePath).Value != plan.SourceRoot || StoragePath.Normalize(destinationPath).Value != plan.DestinationRoot)
            return Result<StorageSyncReport>.Failure(StorageErrors.InvalidContent("The plan was made for different directories."));
        if (!string.Equals(plan.SourceConnectionId, source.ConnectionId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(plan.DestinationConnectionId, destination.ConnectionId, StringComparison.OrdinalIgnoreCase))
            return Result<StorageSyncReport>.Failure(StorageErrors.InvalidContent("The plan was made for different connections."));
        if (!string.Equals(plan.SyncId, options.SyncId, StringComparison.Ordinal))
            return Result<StorageSyncReport>.Failure(StorageErrors.InvalidContent("The plan was made for a different SyncId than the options name."));
        if (!string.Equals(plan.OptionsDigest, options.Fingerprint(), StringComparison.Ordinal))
            return Result<StorageSyncReport>.Failure(StorageErrors.InvalidContent(
                "The plan was made with different options (direction, deletes, conflicts, comparison, time tolerance, safety limits, or verification); plan again with these options."));
        if (!plan.IsApprovable && !options.ApplyWithConflicts)
        {
            var conflicts = plan.Conflicts;
            var named = string.Join(", ", conflicts.Take(10).Select(conflict => $"'{conflict.RelativePath}'"));
            return Result<StorageSyncReport>.Failure(StorageErrors.Conflict(
                $"The plan has {conflicts.Count} unresolved conflict(s): {named}{(conflicts.Count > 10 ? ", …" : string.Empty)}.",
                $"conflicts={conflicts.Count}"));
        }

        // Two applies of one sync in this process would both pass the baseline check; the second waits its turn
        // and is then refused by it. (Across processes the baseline's generation still refuses the later save.)
        using var gate = options.SyncId is { } syncId ? await ApplyGate.EnterAsync(syncId, cancellationToken).ConfigureAwait(false) : null;
        if (options.StateStore is { } store)
        {
            var current = await LoadBaselineAsync(store, options.SyncId!, cancellationToken).ConfigureAwait(false);
            if (current.IsFailure) return Result<StorageSyncReport>.Failure(current.Error!);
            if ((current.Value?.Generation ?? 0) != plan.BaselineGeneration)
                return Result<StorageSyncReport>.Failure(StorageErrors.Conflict("Another run synced these directories after the plan was made; plan again."));
        }

        // Checked like a real apply, then reported without writing anything or saving the baseline.
        if (options.DryRun)
            return Result<StorageSyncReport>.Success(DryRunReport(plan));

        var run = new SyncRun(source, destination, plan, options);
        var cancelled = false;
        try
        {
            await run.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
        }
        var saved = false;
        Error? baselineError = null;
        if (options.StateStore is not null && plan.Direction == StorageSyncDirection.TwoWay)
            (saved, baselineError) = await SaveBaselineAsync(plan, run, options, CancellationToken.None).ConfigureAwait(false);
        return Result<StorageSyncReport>.Success(new StorageSyncReport
        {
            Plan = plan,
            Results = run.Results,
            Cancelled = cancelled,
            BaselineSaved = saved,
            BaselineError = baselineError
        });
    }

    /// <summary>Serializes applies of one sync within this process; an entry is dropped when no one holds or waits for it.</summary>
    private sealed class ApplyGate : IDisposable
    {
        private static readonly Dictionary<string, ApplyGate> Gates = [with(StringComparer.Ordinal)];
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private readonly string _syncId;
        private int _users;

        private ApplyGate(string syncId) => _syncId = syncId;

        public static async Task<ApplyGate> EnterAsync(string syncId, CancellationToken cancellationToken)
        {
            ApplyGate gate;
            lock (Gates)
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
            lock (Gates)
            {
                if (--_users == 0) Gates.Remove(_syncId);
            }
        }

        internal static bool IsHeld(string syncId)
        {
            lock (Gates) return Gates.ContainsKey(syncId);
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

    private static List<string> TypeClashes(StorageDiff diff) =>
        [.. diff.Entries.Where(entry => entry.Reasons.HasFlag(StorageDiffReason.Type)).Select(entry => entry.RelativePath)];

    private static bool Under(List<string> roots, string path, bool insensitive)
    {
        var comparison = insensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return roots.Any(root => path.StartsWith(root + "/", comparison));
    }

    private enum Change { Absent, Unchanged, Created, Modified, Deleted }

    private static Change Classify(StorageSyncIdentity? before, StorageItem? now, TimeSpan tolerance) =>
        (before, now) switch
        {
            (null, null) => Change.Absent,
            (null, _) => Change.Created,
            (_, null) => Change.Deleted,
            _ => before.Matches(now, tolerance) ? Change.Unchanged : Change.Modified
        };

    private static (List<StorageSyncAction> Actions, int Unchanged, Dictionary<string, StorageSyncBaselineEntry> Agreed) PlanTwoWay(
        StorageDiff diff, StorageSyncBaseline baseline, StorageSyncOptions options, Scope scope, List<string> warnings)
    {
        var tolerance = options.Compare.TimeTolerance;
        var actions = new List<StorageSyncAction>();
        var agreed = new Dictionary<string, StorageSyncBaselineEntry>(StringComparer.Ordinal);
        var unchanged = 0;
        var hasBaseline = options.StateStore is not null;
        var insensitive = diff.CaseInsensitive;
        var baselineByKey = new Dictionary<string, (string Path, StorageSyncBaselineEntry Entry)>(StringComparer.Ordinal);
        foreach (var (path, value) in baseline.Entries.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!baselineByKey.TryAdd(StorageCompare.KeyOf(path, insensitive), (path, value)))
                warnings.Add($"The baseline holds '{baselineByKey[StorageCompare.KeyOf(path, insensitive)].Path}' and '{path}', the same name where case is ignored; the first was used.");
        }
        var entriesByKey = diff.Entries.ToDictionary(entry => StorageCompare.KeyOf(entry.RelativePath, insensitive), StringComparer.Ordinal);
        var clashes = TypeClashes(diff);

        foreach (var key in entriesByKey.Keys.Union(baselineByKey.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            entriesByKey.TryGetValue(key, out var entry);
            var base_ = baselineByKey.TryGetValue(key, out var b) ? b.Entry : null;
            // Steps name a path as the source spells it: the diff's spelling whenever it has the path (the source's
            // own, or for an item only at the destination the source's spelling of its folders), otherwise the one the
            // baseline recorded. Baseline entries are matched ignoring case where case is ignored, so the entry is
            // found again whatever happens to the step.
            var path = entry?.RelativePath ?? b.Path;
            var there = entry?.DestinationRelativePath ?? entry?.RelativePath ?? path;
            var isDirectory = entry?.IsDirectory ?? base_?.IsDirectory ?? false;
            void CarryOver()
            {
                if (base_ is not null) agreed[b.Path] = base_;
            }

            if (Under(clashes, path, insensitive)) { CarryOver(); continue; }
            if (entry is { Reasons: var r } && r.HasFlag(StorageDiffReason.Type))
            {
                warnings.Add($"'{path}' is a file on one side and a directory on the other; it and everything below it were left alone.");
                CarryOver();
                continue;
            }
            // Left out on either side, or by the current filters: nothing is known to have changed, so the entry
            // is kept for when the path takes part again.
            if (scope.LeftOut(path, there, isDirectory))
            {
                if (entry is null && scope.GoneFromBothSides(path, there, isDirectory)) continue;
                CarryOver();
                continue;
            }
            if (isDirectory)
            {
                PlanDirectory(actions, agreed, entry, path, hasBaseline && base_ is { IsDirectory: true }, options);
                continue;
            }

            var left = Classify(base_?.Source, entry?.Source, tolerance);
            var right = Classify(base_?.Destination, entry?.Destination, tolerance);
            if (!hasBaseline)
            {
                // Without a baseline nothing can be known to be deleted: missing files are copied, differences conflict.
                left = entry?.Source is null ? Change.Absent : Change.Created;
                right = entry?.Destination is null ? Change.Absent : Change.Created;
            }

            switch (left, right)
            {
                case (Change.Unchanged or Change.Absent, Change.Unchanged or Change.Absent) when entry?.Kind != StorageDiffKind.OnlyInSource && entry?.Kind != StorageDiffKind.OnlyInDestination:
                    if (entry is null) break;
                    // Neither side changed since the baseline, yet their content differs: the baseline cannot be
                    // trusted for this path, so it is a conflict rather than silently "in sync".
                    if (entry.Kind == StorageDiffKind.Different && (entry.Reasons & (StorageDiffReason.Size | StorageDiffReason.Checksum)) != 0)
                    {
                        Resolve(actions, entry, path, StorageSyncConflictKind.BothModified, options);
                        break;
                    }
                    unchanged++;
                    Agree(agreed, entry, path);
                    break;
                case (Change.Created or Change.Modified, Change.Unchanged or Change.Absent):
                    actions.Add(Copy(entry!, StorageSyncActionKind.CopyToDestination, path: path));
                    break;
                case (Change.Unchanged or Change.Absent, Change.Created or Change.Modified):
                    actions.Add(Copy(entry!, StorageSyncActionKind.CopyToSource, path: path));
                    break;
                case (Change.Deleted, Change.Unchanged):
                    actions.Add(options.PropagateDeletes
                        ? new StorageSyncAction { RelativePath = path, DestinationRelativePath = DestinationSpelling(entry, path), Kind = StorageSyncActionKind.DeleteFromDestination, Destination = StorageSyncIdentity.Of(entry!.Destination) }
                        : Copy(entry!, StorageSyncActionKind.CopyToSource, path: path));
                    break;
                case (Change.Unchanged, Change.Deleted):
                    actions.Add(options.PropagateDeletes
                        ? new StorageSyncAction { RelativePath = path, Kind = StorageSyncActionKind.DeleteFromSource, Source = StorageSyncIdentity.Of(entry!.Source) }
                        : Copy(entry!, StorageSyncActionKind.CopyToDestination, path: path));
                    break;
                case (Change.Deleted, Change.Deleted):
                case (Change.Deleted, Change.Absent):
                case (Change.Absent, Change.Deleted):
                    break;
                case (Change.Created, Change.Created) or (Change.Modified, Change.Modified) or (Change.Created, Change.Modified) or (Change.Modified, Change.Created):
                    if (entry!.Kind == StorageDiffKind.Same)
                    {
                        unchanged++;
                        Agree(agreed, entry, path);
                        break;
                    }
                    Resolve(actions, entry, path, left == Change.Modified && right == Change.Modified ? StorageSyncConflictKind.BothModified : StorageSyncConflictKind.BothCreated, options);
                    break;
                case (Change.Deleted, Change.Created or Change.Modified):
                case (Change.Created or Change.Modified, Change.Deleted):
                    ResolveDeleteVersusModify(actions, entry!, path, deletedOnSource: left == Change.Deleted, options);
                    break;
                default:
                    if (entry is not null && entry.Kind == StorageDiffKind.Same)
                    {
                        unchanged++;
                        Agree(agreed, entry, path);
                    }
                    break;
            }
        }
        KeepLinksWhole(actions, diff, baselineByKey, agreed, warnings);
        DropUnsafeDirectoryDeletes(actions, diff, baselineByKey, agreed);
        return (actions, unchanged, agreed);
    }

    private static void Agree(Dictionary<string, StorageSyncBaselineEntry> agreed, StorageDiffEntry entry, string path) =>
        agreed[path] = new StorageSyncBaselineEntry(StorageSyncIdentity.Of(entry.Source), StorageSyncIdentity.Of(entry.Destination));

    /// <summary>
    /// Plans a directory in a two-way sync: created on the side that lacks it, or — when the baseline had it on
    /// both sides — deleted where the other side deleted it (only once everything in it goes too).
    /// </summary>
    private static void PlanDirectory(
        List<StorageSyncAction> actions,
        Dictionary<string, StorageSyncBaselineEntry> agreed,
        StorageDiffEntry? entry,
        string path,
        bool wasOnBothSides,
        StorageSyncOptions options)
    {
        var atSource = entry?.Source is not null;
        var atDestination = entry?.Destination is not null;
        var there = DestinationSpelling(entry, path);
        if (atSource && atDestination)
        {
            agreed[path] = new StorageSyncBaselineEntry(null, null, IsDirectory: true);
            return;
        }
        if (atSource)
        {
            actions.Add(wasOnBothSides && options.PropagateDeletes
                ? new StorageSyncAction { RelativePath = path, Kind = StorageSyncActionKind.DeleteFromSource }
                : new StorageSyncAction { RelativePath = path, DestinationRelativePath = there, Kind = StorageSyncActionKind.CreateDirectory });
        }
        else if (atDestination)
        {
            actions.Add(wasOnBothSides && options.PropagateDeletes
                ? new StorageSyncAction { RelativePath = path, DestinationRelativePath = there, Kind = StorageSyncActionKind.DeleteFromDestination }
                : new StorageSyncAction { RelativePath = path, Kind = StorageSyncActionKind.CreateDirectoryAtSource });
        }
    }

    /// <summary>
    /// Takes out every step that would change a side through a link it followed (<see cref="StorageLinkHandling.Follow"/>):
    /// a delete of the link or of anything below a followed link to a folder, and a copy or rename onto a followed link
    /// to a file, which would replace the link (or write into its target). The path keeps its baseline entry, so the
    /// next run sees it the same way. Copies into a followed folder are kept: they land in the link's target.
    /// </summary>
    private static void KeepLinksWhole(
        List<StorageSyncAction> actions,
        StorageDiff diff,
        Dictionary<string, (string Path, StorageSyncBaselineEntry Entry)>? baselineByKey,
        Dictionary<string, StorageSyncBaselineEntry>? agreed,
        List<string> warnings)
    {
        var dropped = new HashSet<string>(diff.CaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        bool Through(StorageSyncAction action)
        {
            var there = action.DestinationRelativePath ?? action.RelativePath;
            return action.Kind switch
            {
                StorageSyncActionKind.DeleteFromDestination => diff.DestinationLinks.Through(there),
                StorageSyncActionKind.DeleteFromSource => diff.SourceLinks.Through(action.RelativePath),
                StorageSyncActionKind.CopyToDestination or StorageSyncActionKind.RenameAtDestination => diff.DestinationLinks.IsFileLink(there),
                StorageSyncActionKind.CopyToSource when action.TargetPath is null => diff.SourceLinks.IsFileLink(action.RelativePath),
                _ => false
            };
        }
        foreach (var action in actions.Where(Through).ToList())
        {
            actions.Remove(action);
            dropped.Add(action.RelativePath);
            var side = action.Kind is StorageSyncActionKind.DeleteFromSource or StorageSyncActionKind.CopyToSource ? "source" : "destination";
            warnings.Add($"'{action.RelativePath}' is a followed link at the {side}, or lies in one; nothing is deleted or replaced through a link, so it was left alone.");
            if (baselineByKey is not null && agreed is not null &&
                baselineByKey.TryGetValue(StorageCompare.KeyOf(action.RelativePath, diff.CaseInsensitive), out var previous))
                agreed[previous.Path] = previous.Entry;
        }
        // A kept conflict copy is only brought back to the source when its rename was taken.
        if (dropped.Count > 0)
            actions.RemoveAll(action => action.Kind == StorageSyncActionKind.CopyToSource && action.Conflict is not null &&
                                        action.TargetPath is { } target && dropped.Contains(target));
    }

    /// <summary>A copy of an item reached through a followed link reads its content where the link leads.</summary>
    private static List<StorageSyncAction> ReadThroughLinks(List<StorageSyncAction> actions, StorageDiff diff) =>
        [.. actions.Select(action => action.Kind switch
        {
            StorageSyncActionKind.CopyToDestination when diff.SourceLinks.ReadPath(action.RelativePath) is { } read => action with { ReadPath = read },
            StorageSyncActionKind.CopyToSource when action.TargetPath is null &&
                diff.DestinationLinks.ReadPath(action.DestinationRelativePath ?? action.RelativePath) is { } read => action with { ReadPath = read },
            _ => action
        })];

    /// <summary>
    /// Keeps a directory deletion only when everything inside it on that side is deleted in the same plan and
    /// nothing inside it was left out; otherwise the directory stays (and keeps its baseline entry, so the side
    /// that deleted it does not get it back), and the kept items inside are synced as usual.
    /// </summary>
    private static void DropUnsafeDirectoryDeletes(
        List<StorageSyncAction> actions,
        StorageDiff diff,
        Dictionary<string, (string Path, StorageSyncBaselineEntry Entry)>? baselineByKey,
        Dictionary<string, StorageSyncBaselineEntry>? agreed)
    {
        var comparer = diff.CaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var directories = diff.Entries.Where(entry => entry.IsDirectory).Select(entry => entry.RelativePath).ToHashSet(comparer);
        foreach (var kind in new[] { StorageSyncActionKind.DeleteFromSource, StorageSyncActionKind.DeleteFromDestination })
        {
            var onSource = kind == StorageSyncActionKind.DeleteFromSource;
            var exclusions = onSource ? diff.SourceExclusions : diff.DestinationExclusions;
            var deleted = actions.Where(action => action.Kind == kind).Select(action => action.RelativePath).ToHashSet(comparer);
            var folderDeletes = actions.Where(action => action.Kind == kind && directories.Contains(action.RelativePath)).ToList();
            if (folderDeletes.Count == 0) continue;

            // Every folder that still holds something on this side after the plan: the parents of what stays.
            var kept = new HashSet<string>(comparer);
            void KeepParents(string path)
            {
                for (var parent = StorageExclusions.Parent(path); parent.Length > 0 && kept.Add(parent); parent = StorageExclusions.Parent(parent))
                {
                }
            }
            foreach (var entry in diff.Entries)
            {
                if ((onSource ? entry.Source : entry.Destination) is not null && !deleted.Contains(entry.RelativePath))
                    KeepParents(entry.RelativePath);
            }

            foreach (var directory in folderDeletes.OrderByDescending(action => action.RelativePath.Count(c => c == '/')))
            {
                var there = onSource ? directory.RelativePath : directory.DestinationRelativePath ?? directory.RelativePath;
                if (!kept.Contains(directory.RelativePath) && !exclusions.Keeps(there)) continue;
                actions.Remove(directory);
                KeepParents(directory.RelativePath);
                if (baselineByKey is not null && agreed is not null &&
                    baselineByKey.TryGetValue(StorageCompare.KeyOf(directory.RelativePath, diff.CaseInsensitive), out var previous))
                    agreed[previous.Path] = previous.Entry;
            }
        }
    }

    private static void Resolve(List<StorageSyncAction> actions, StorageDiffEntry entry, string path, StorageSyncConflictKind kind, StorageSyncOptions options)
    {
        switch (options.ConflictPolicy)
        {
            case StorageSyncConflictPolicy.NewerWins:
                var a = StorageCompare.EffectiveModified(entry.Source!);
                var b = StorageCompare.EffectiveModified(entry.Destination!);
                if (a is { } left && b is { } right && (left - right).Duration() > options.Compare.TimeTolerance)
                {
                    actions.Add(Copy(entry, left > right ? StorageSyncActionKind.CopyToDestination : StorageSyncActionKind.CopyToSource, kind, path));
                    return;
                }
                break;
            case StorageSyncConflictPolicy.KeepBoth:
                var theirs = StorageSyncIdentity.Of(entry.Destination);
                var conflictPath = ConflictPath(path, theirs!);
                var there = entry.DestinationRelativePath ?? entry.RelativePath;
                var conflictThere = Sibling(there, conflictPath);
                actions.Add(new StorageSyncAction { RelativePath = path, DestinationRelativePath = DestinationSpelling(entry, path), Kind = StorageSyncActionKind.RenameAtDestination, TargetPath = conflictPath, Destination = theirs, Conflict = kind });
                actions.Add(Copy(entry, StorageSyncActionKind.CopyToDestination, kind, path) with { Destination = null });
                actions.Add(new StorageSyncAction
                {
                    RelativePath = conflictPath,
                    DestinationRelativePath = string.Equals(conflictThere, conflictPath, StringComparison.Ordinal) ? null : conflictThere,
                    Kind = StorageSyncActionKind.CopyToSource,
                    Conflict = kind,
                    Bytes = entry.Destination?.Size,
                    Destination = theirs,
                    TargetPath = path
                });
                return;
        }
        actions.Add(new StorageSyncAction
        {
            RelativePath = path,
            DestinationRelativePath = DestinationSpelling(entry, path),
            Kind = StorageSyncActionKind.Conflict,
            Reason = entry.Reasons,
            Source = StorageSyncIdentity.Of(entry.Source),
            Destination = StorageSyncIdentity.Of(entry.Destination),
            Conflict = kind
        });
    }

    private static void ResolveDeleteVersusModify(List<StorageSyncAction> actions, StorageDiffEntry entry, string path, bool deletedOnSource, StorageSyncOptions options)
    {
        if (options.ConflictPolicy is StorageSyncConflictPolicy.KeepBoth or StorageSyncConflictPolicy.NewerWins)
        {
            // Keep the edit: copy the modified file back to the side that deleted it.
            actions.Add(Copy(entry, deletedOnSource ? StorageSyncActionKind.CopyToSource : StorageSyncActionKind.CopyToDestination, StorageSyncConflictKind.DeleteVersusModify, path));
            return;
        }
        actions.Add(new StorageSyncAction
        {
            RelativePath = path,
            DestinationRelativePath = DestinationSpelling(entry, path),
            Kind = StorageSyncActionKind.Conflict,
            Source = StorageSyncIdentity.Of(entry.Source),
            Destination = StorageSyncIdentity.Of(entry.Destination),
            Conflict = StorageSyncConflictKind.DeleteVersusModify
        });
    }

    /// <summary>
    /// <c>name (conflict xxxxxxxx).ext</c>, from the kept version's identity so repeated runs pick the same name;
    /// a compound extension such as <c>.tar.gz</c> stays whole.
    /// </summary>
    internal static string ConflictPath(string path, StorageSyncIdentity identity, int attempt = 1)
    {
        var seed = $"{identity.Size}|{identity.Modified?.UtcTicks}|{identity.ETag}|{identity.VersionId}";
        var tag = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seed)))[..8];
        return ConflictResolverName(path, attempt == 1 ? $"conflict {tag}" : $"conflict {tag} {attempt}");
    }

    private static string ConflictResolverName(string path, string label)
    {
        var slash = path.LastIndexOf('/');
        var directory = slash < 0 ? string.Empty : path[..(slash + 1)];
        var name = path[(slash + 1)..];
        var dot = name.LastIndexOf('.');
        if (dot > 4 && name.LastIndexOf(".tar", dot, StringComparison.OrdinalIgnoreCase) == dot - 4) dot -= 4;
        var (stem, extension) = dot > 0 ? (name[..dot], name[dot..]) : (name, string.Empty);
        return $"{directory}{stem} ({label}){extension}";
    }

    /// <summary>A file beside <paramref name="path"/>, named like <paramref name="named"/>.</summary>
    private static string Sibling(string path, string named)
    {
        var slash = path.LastIndexOf('/');
        var name = named[(named.LastIndexOf('/') + 1)..];
        return slash < 0 ? name : $"{path[..slash]}/{name}";
    }

    /// <summary>Withholds every deletion when a limit or an empty side makes the run look unsafe.</summary>
    private static List<StorageSyncAction> ApplyDeletionSafety(
        List<StorageSyncAction> actions,
        StorageSyncBaseline baseline,
        StorageTree source,
        StorageTree destination,
        StorageSyncOptions options,
        List<string> warnings)
    {
        var deletes = actions.Where(action => action.Kind is StorageSyncActionKind.DeleteFromDestination or StorageSyncActionKind.DeleteFromSource).ToList();
        if (deletes.Count == 0) return actions;

        static int Files(StorageTree tree) => tree.Items.Values.Count(item => item.ItemType != StorageItemType.Directory);
        // Every file is its own step; a directory step only removes an emptied folder, so it counts for nothing.
        var destinationDeletes = deletes.Count(action => action.Kind == StorageSyncActionKind.DeleteFromDestination && action.Destination is not null);
        var sourceDeletes = deletes.Count(action => action.Kind == StorageSyncActionKind.DeleteFromSource && action.Source is not null);
        var sourceFiles = Files(source);
        var destinationFiles = Files(destination);

        string? reason = null;
        var baselineFiles = baseline.Entries.Values.Count(entry => !entry.IsDirectory);
        if (!options.AllowEmptySide)
        {
            if (sourceFiles == 0 && (destinationFiles > 0 || baselineFiles > 0))
                reason = "the source is unexpectedly empty";
            else if (destinationFiles == 0 && options.Direction == StorageSyncDirection.TwoWay && baselineFiles > 0)
                reason = "the destination is unexpectedly empty";
        }
        var total = destinationDeletes + sourceDeletes;
        if (reason is null && options.MaxDeletes is { } maxDeletes && total > maxDeletes)
            reason = $"{total} file deletion(s) exceed MaxDeletes ({maxDeletes})";
        if (reason is null && options.MaxDeletePercent is { } maxPercent)
        {
            var destinationShare = destinationFiles == 0 ? 0 : destinationDeletes * 100.0 / destinationFiles;
            var sourceShare = sourceFiles == 0 ? 0 : sourceDeletes * 100.0 / sourceFiles;
            var share = Math.Max(destinationShare, sourceShare);
            if (share > maxPercent)
                reason = $"deleting {Percent(share, maxPercent)}% of a side's files exceeds MaxDeletePercent ({maxPercent.ToString(CultureInfo.InvariantCulture)}%)";
        }
        if (reason is null) return actions;
        var folders = deletes.Count - total;
        warnings.Add($"All {total} file deletion(s){(folders > 0 ? $" and {folders} folder deletion(s)" : string.Empty)} were withheld because {reason}.");
        return [.. actions.Select(action => action.Kind is StorageSyncActionKind.DeleteFromDestination or StorageSyncActionKind.DeleteFromSource
            ? action with { WithheldReason = reason }
            : action)];
    }

    /// <summary>A share with as many decimals as it takes to differ from the limit it exceeds ("50.01", never "50").</summary>
    private static string Percent(double share, double limit)
    {
        var text = share.ToString("0.#", CultureInfo.InvariantCulture);
        for (var digits = 2; digits <= 6 && double.Parse(text, CultureInfo.InvariantCulture) <= limit; digits++)
            text = share.ToString("0." + new string('#', digits), CultureInfo.InvariantCulture);
        return text;
    }

    /// <summary>Directories first (shallowest first), then renames, copies, and deletions (deepest first).</summary>
    private static IReadOnlyList<StorageSyncAction> Order(List<StorageSyncAction> actions)
    {
        static int Rank(StorageSyncActionKind kind) => kind switch
        {
            StorageSyncActionKind.CreateDirectory or StorageSyncActionKind.CreateDirectoryAtSource => 0,
            StorageSyncActionKind.RenameAtDestination => 1,
            StorageSyncActionKind.CopyToDestination or StorageSyncActionKind.CopyToSource => 2,
            StorageSyncActionKind.Conflict => 3,
            _ => 4
        };
        static int Depth(StorageSyncAction action) => action.RelativePath.Length == 0 ? -1 : action.RelativePath.Count(c => c == '/');
        return [.. actions
            .Select((action, index) => (action, index))
            .OrderBy(pair => Rank(pair.action.Kind))
            .ThenBy(pair => Rank(pair.action.Kind) switch { 0 => Depth(pair.action), 4 => -Depth(pair.action), _ => 0 })
            .ThenBy(pair => pair.index)
            .Select(pair => pair.action)];
    }

    // ---------------------------------------------------------------- applying

    /// <summary>Runs a plan's steps and records their outcomes, and what each applied step left on both sides.</summary>
    private sealed class SyncRun(IStorageService source, IStorageService destination, StorageSyncPlan plan, StorageSyncOptions options)
    {
        private readonly StorageSyncActionResult[] _results = [.. plan.Actions.Select(action => new StorageSyncActionResult(action,
            action.WithheldReason is not null ? StorageSyncActionOutcome.Withheld : StorageSyncActionOutcome.NotRun))];
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _renamed = new(StringComparer.Ordinal);

        public IReadOnlyList<StorageSyncActionResult> Results => _results;

        /// <summary>Paths an applied step brought into sync, with the versions it wrote or created.</summary>
        public System.Collections.Concurrent.ConcurrentDictionary<string, StorageSyncBaselineEntry> Synced { get; } = new(StringComparer.Ordinal);

        /// <summary>Paths an applied step deleted.</summary>
        public System.Collections.Concurrent.ConcurrentDictionary<string, bool> Removed { get; } = new(StringComparer.Ordinal);

        public async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var indexes = Enumerable.Range(0, plan.Actions.Count).ToList();
            // Only for recognising the version a source had; whatever a step deletes, overwrites, or renames must
            // be exactly the version planned (see DeleteAsync, CopyAsync, RenameAsync).
            var tolerance = options.Compare.TimeTolerance;

            // Directories and renames run in order; a rename must land before the copies that follow it.
            foreach (var i in indexes.Where(i => plan.Actions[i].Kind is StorageSyncActionKind.CreateDirectory or StorageSyncActionKind.CreateDirectoryAtSource or StorageSyncActionKind.RenameAtDestination))
            {
                if (stop.IsCancellationRequested) break;
                await RunAsync(i, stop, tolerance).ConfigureAwait(false);
            }
            await Parallel.ForEachAsync(
                indexes.Where(i => plan.Actions[i].Kind is StorageSyncActionKind.CopyToDestination or StorageSyncActionKind.CopyToSource),
                new ParallelOptions { MaxDegreeOfParallelism = options.MaxConcurrency },
                async (i, _) =>
                {
                    if (!stop.IsCancellationRequested) await RunAsync(i, stop, tolerance).ConfigureAwait(false);
                }).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            // A failed step means the two sides do not match yet, so deleting could remove the only good copy.
            var failed = _results.Count(result => result.Outcome is StorageSyncActionOutcome.Failed or StorageSyncActionOutcome.Stale);
            foreach (var i in indexes.Where(i => plan.Actions[i].Kind is StorageSyncActionKind.DeleteFromDestination or StorageSyncActionKind.DeleteFromSource))
            {
                if (stop.IsCancellationRequested) break;
                if (_results[i].Outcome == StorageSyncActionOutcome.Withheld) continue;
                if (failed > 0)
                {
                    _results[i] = _results[i] with
                    {
                        Outcome = StorageSyncActionOutcome.Withheld,
                        Action = plan.Actions[i] with { WithheldReason = $"{failed} other step(s) failed or found their item changed in this run" }
                    };
                    continue;
                }
                await RunAsync(i, stop, tolerance).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        private async Task RunAsync(int index, CancellationTokenSource stop, TimeSpan tolerance)
        {
            var action = plan.Actions[index];
            var attempts = 0;
            Result result;
            try
            {
                while (true)
                {
                    attempts++;
                    result = await StepAsync(action, tolerance, stop.Token).ConfigureAwait(false);
                    if (result.IsSuccess || StorageErrorInfo.DestinationCommitted(result.Error) || !StorageErrorInfo.IsTransient(result.Error) ||
                        attempts > options.ItemRetries || stop.IsCancellationRequested)
                        break;
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempts - 1)), stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // Stopped by the caller, or by another step failing with ContinueOnError off: this step stays NotRun.
                return;
            }
            catch (Exception error)
            {
                // A provider or store that throws fails this step only; the run, its report, and the baseline go on.
                result = Result.Failure(StorageErrors.FromException(error, $"Sync '{action.RelativePath}'"));
            }
            var committed = StorageErrorInfo.DestinationCommitted(result.Error);
            // A step that ended because the run was stopping did not fail on its own: it stays NotRun. One that
            // failed for another reason meanwhile is reported as it ended.
            if (result.IsFailure && !committed && stop.IsCancellationRequested && result.Error!.Code == StorageErrors.CancelledCode) return;
            var outcome = result.IsSuccess || committed
                ? StorageSyncActionOutcome.Applied
                : IsStale(result.Error) ? StorageSyncActionOutcome.Stale : StorageSyncActionOutcome.Failed;
            _results[index] = new StorageSyncActionResult(action, outcome, result.Error, attempts);
            if (outcome != StorageSyncActionOutcome.Applied && !options.ContinueOnError)
                await stop.CancelAsync().ConfigureAwait(false);
        }

        private string SourcePath(string relative) => Join(plan.SourceRoot, relative);
        private static string? ReadPath(StorageSyncAction action) => action.ReadPath is { } read ? StoragePath.Normalize(read).Value : null;
        private string DestinationPath(StorageSyncAction action) => Join(plan.DestinationRoot, action.DestinationRelativePath ?? action.RelativePath);
        private string DestinationPath(string relative) => Join(plan.DestinationRoot, relative);

        private async Task<Result> StepAsync(StorageSyncAction action, TimeSpan tolerance, CancellationToken cancellationToken)
        {
            switch (action.Kind)
            {
                case StorageSyncActionKind.CreateDirectory:
                {
                    var created = await destination.CreateDirectoryAsync(DestinationPath(action), cancellationToken).ConfigureAwait(false);
                    if (created.IsSuccess && action.RelativePath.Length > 0) Synced[action.RelativePath] = new StorageSyncBaselineEntry(null, null, IsDirectory: true);
                    return created;
                }
                case StorageSyncActionKind.CreateDirectoryAtSource:
                {
                    var created = await source.CreateDirectoryAsync(SourcePath(action.RelativePath), cancellationToken).ConfigureAwait(false);
                    if (created.IsSuccess) Synced[action.RelativePath] = new StorageSyncBaselineEntry(null, null, IsDirectory: true);
                    return created;
                }
                case StorageSyncActionKind.RenameAtDestination:
                    return await RenameAsync(action, cancellationToken).ConfigureAwait(false);
                case StorageSyncActionKind.CopyToDestination:
                    return await CopyAsync(source, ReadPath(action) ?? SourcePath(action.RelativePath), action.Source, destination, DestinationPath(action), action.Destination,
                        checkFrom: true, towardDestination: true, action.RelativePath, tolerance, cancellationToken).ConfigureAwait(false);
                case StorageSyncActionKind.CopyToSource:
                {
                    // A kept conflict copy was renamed in this run (possibly to a numbered name), which may also give
                    // it a new identity on object stores.
                    var chained = action.Conflict is not null && action.TargetPath is not null;
                    var relative = chained ? _renamed.GetValueOrDefault(action.RelativePath, action.RelativePath) : action.RelativePath;
                    var from = chained ? DestinationPath(Sibling(action.DestinationRelativePath ?? action.RelativePath, relative)) : ReadPath(action) ?? DestinationPath(action);
                    return await CopyAsync(destination, from, action.Destination, source, SourcePath(relative), chained ? null : action.Source,
                        checkFrom: !chained, towardDestination: false, relative, tolerance, cancellationToken).ConfigureAwait(false);
                }
                case StorageSyncActionKind.DeleteFromDestination:
                {
                    var deleted = await DeleteAsync(destination, DestinationPath(action), action.Destination, cancellationToken).ConfigureAwait(false);
                    if (deleted.IsSuccess) Removed[action.RelativePath] = true;
                    return deleted;
                }
                case StorageSyncActionKind.DeleteFromSource:
                {
                    var deleted = await DeleteAsync(source, SourcePath(action.RelativePath), action.Source, cancellationToken).ConfigureAwait(false);
                    if (deleted.IsSuccess) Removed[action.RelativePath] = true;
                    return deleted;
                }
                default:
                    return Result.Success();
            }
        }

        /// <summary>
        /// Moves the destination's version aside as a kept conflict copy. The planned name is taken when it is free
        /// on both sides; otherwise a numbered one, so an old conflict copy never blocks the run. Only the version
        /// that was planned is moved: the move is pinned to its ETag and version, and where the connection cannot
        /// pin a move, the version is copied (pinned) and then deleted under the same condition.
        /// </summary>
        private async Task<Result> RenameAsync(StorageSyncAction action, CancellationToken cancellationToken)
        {
            var current = await Current(destination, DestinationPath(action), cancellationToken).ConfigureAwait(false);
            if (current.IsFailure) return Result.Failure(current.Error!);
            if (!action.Destination!.Matches(current.Value, TimeSpan.Zero)) return Stale(action.RelativePath);
            var there = action.DestinationRelativePath ?? action.RelativePath;
            for (var attempt = 1; attempt <= 20; attempt++)
            {
                var target = attempt == 1 ? action.TargetPath! : ConflictPath(action.RelativePath, action.Destination, attempt);
                var atDestination = await Current(destination, DestinationPath(Sibling(there, target)), cancellationToken).ConfigureAwait(false);
                if (atDestination.IsFailure) return Result.Failure(atDestination.Error!);
                var atSource = await Current(source, SourcePath(target), cancellationToken).ConfigureAwait(false);
                if (atSource.IsFailure) return Result.Failure(atSource.Error!);
                if (atDestination.Value is not null || atSource.Value is not null) continue;
                var moved = await MoveVersionAsync(DestinationPath(action), DestinationPath(Sibling(there, target)), current.Value!, cancellationToken).ConfigureAwait(false);
                if (moved.IsSuccess || StorageErrorInfo.DestinationCommitted(moved.Error)) _renamed[action.TargetPath!] = target;
                return moved;
            }
            return Result.Failure(StorageErrors.Conflict($"No free name was found for the conflict copy of '{action.RelativePath}'."));
        }

        private async Task<Result> MoveVersionAsync(string fromPath, string toPath, StorageItem version, CancellationToken cancellationToken)
        {
            var pinned = version.ETag is not null || version.VersionId is not null;
            var moved = await destination.MoveAsync(fromPath, toPath, new StorageTransferOptions
            {
                Overwrite = false,
                ExpectedSourceETag = version.ETag,
                SourceVersionId = version.VersionId
            }, cancellationToken).ConfigureAwait(false);
            if (moved.IsFailure && moved.Error!.Code == StorageErrors.UnsupportedCode && pinned)
                return await CopyThenDeleteAsync(fromPath, toPath, version, cancellationToken).ConfigureAwait(false);
            if (moved.IsFailure && moved.Error!.Code == StorageErrors.ConflictCode) return Stale(fromPath);
            return moved;
        }

        /// <summary>
        /// A move for connections that cannot pin one: the planned version is copied through staging (read pinned,
        /// and checked unchanged after it streamed), created only where nothing is, and the original is then
        /// deleted only while it is still that version.
        /// </summary>
        private async Task<Result> CopyThenDeleteAsync(string fromPath, string toPath, StorageItem version, CancellationToken cancellationToken)
        {
            var written = await StagedWriter.WriteAsync(
                destination,
                new StagedWriteRequest
                {
                    Path = toPath,
                    Upload = new StorageUploadOptions { CreateParents = true, ContentType = version.ContentType, Metadata = version.Metadata },
                    ExpectedLength = version.Size
                },
                (offset, token) => destination.DownloadAsync(fromPath, new StorageDownloadOptions { Offset = offset, VersionId = version.VersionId }, token),
                cancellationToken).ConfigureAwait(false);
            if (written.Cancelled) cancellationToken.ThrowIfCancellationRequested();
            if (!written.IsSuccess) return Result.Failure(written.Error!);
            var identity = StorageSyncIdentity.Of(version)!;
            var after = await Current(destination, fromPath, cancellationToken).ConfigureAwait(false);
            if (after.IsFailure || !identity.Matches(after.Value, TimeSpan.Zero))
            {
                await StagedWriter.DeleteAsync(destination, written.Content!.StagingPath).ConfigureAwait(false);
                return after.IsFailure ? Result.Failure(after.Error!) : Stale(fromPath);
            }
            var (promoted, _) = await StagedWriter.PromoteAsync(destination, written.Content!.StagingPath, toPath, overwrite: false, condition: null, createParents: true, CancellationToken.None).ConfigureAwait(false);
            if (promoted.IsFailure && !StorageErrorInfo.DestinationCommitted(promoted.Error))
            {
                await StagedWriter.DeleteAsync(destination, written.Content.StagingPath).ConfigureAwait(false);
                return promoted.Error!.Code == StorageErrors.ConflictCode ? Stale(toPath) : promoted;
            }
            var removed = await DeleteAsync(destination, fromPath, identity, CancellationToken.None).ConfigureAwait(false);
            if (removed.IsFailure)
                return Result.Failure(StorageErrors.Conflict(
                    $"The conflict copy '{toPath}' was made, but '{fromPath}' changed or could not be removed, so it was kept: {removed.Error!.Message}",
                    $"{StaleKey}=true"));
            return promoted;
        }

        /// <summary>Copies one file through a staged, conditional (and optionally verified) write, and records the pair.</summary>
        private async Task<Result> CopyAsync(
            IStorageService from,
            string fromPath,
            StorageSyncIdentity? planned,
            IStorageService to,
            string toPath,
            StorageSyncIdentity? plannedTarget,
            bool checkFrom,
            bool towardDestination,
            string relative,
            TimeSpan tolerance,
            CancellationToken cancellationToken)
        {
            var read = await Current(from, fromPath, cancellationToken).ConfigureAwait(false);
            if (read.IsFailure) return Result.Failure(read.Error!);
            var fromItem = read.Value;
            if (fromItem is null || fromItem.ItemType != StorageItemType.File || (checkFrom && planned is not null && !planned.Matches(fromItem, tolerance)))
                return Stale(fromPath);
            var target = await Current(to, toPath, cancellationToken).ConfigureAwait(false);
            if (target.IsFailure) return Result.Failure(target.Error!);
            // What a copy replaces must be exactly the version planned: a same-size edit within the time tolerance
            // is still an edit (FTP listings keep minutes), and it is never overwritten.
            if (plannedTarget is null ? target.Value is not null : !plannedTarget.Matches(target.Value, TimeSpan.Zero))
                return Stale(toPath);

            var modified = StorageCompare.EffectiveModified(fromItem);
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            var keepTimeInMetadata = options.PreserveTimestamps && modified is not null &&
                !to.Capabilities.Supports(StorageFeature.SetTimestamps) && to.Capabilities.Supports(StorageFeature.MetadataWrite);
            if (keepTimeInMetadata)
                metadata[StorageCompareOptions.ModifiedMetadataKey] = modified!.Value.ToString("O", CultureInfo.InvariantCulture);

            var written = await StagedWriter.WriteAsync(
                to,
                new StagedWriteRequest
                {
                    Path = toPath,
                    Upload = new StorageUploadOptions
                    {
                        CreateParents = true,
                        ContentType = fromItem.ContentType,
                        Metadata = metadata,
                        Progress = options.Progress is { } progress ? new PathProgress(progress, fromPath) : null
                    },
                    ExpectedLength = fromItem.Size,
                    Verify = options.Verify
                },
                (offset, token) => from.DownloadAsync(fromPath, new StorageDownloadOptions { Offset = offset, VersionId = fromItem.VersionId }, token),
                cancellationToken).ConfigureAwait(false);
            if (written.Cancelled) cancellationToken.ThrowIfCancellationRequested();
            if (!written.IsSuccess) return Result.Failure(written.Error!);
            var staged = written.Content!;

            async Task<Result> Abandon(Result why)
            {
                await StagedWriter.DeleteAsync(to, staged.StagingPath).ConfigureAwait(false);
                return why;
            }

            // Without a pinned version the source is read again: it must not have changed while it streamed, by its
            // ETag, or by size and time where it has none (SFTP, FTP).
            if (fromItem.VersionId is null)
            {
                var after = await Current(from, fromPath, cancellationToken).ConfigureAwait(false);
                if (after.IsFailure) return await Abandon(Result.Failure(after.Error!)).ConfigureAwait(false);
                if (!StorageSyncIdentity.Of(fromItem)!.Matches(after.Value, TimeSpan.Zero)) return await Abandon(Stale(fromPath)).ConfigureAwait(false);
            }
            // The target must still be what was planned right before it is replaced; the promote checks it again.
            StorageMutationCondition? condition = null;
            if (plannedTarget is not null)
            {
                var again = await Current(to, toPath, cancellationToken).ConfigureAwait(false);
                if (again.IsFailure) return await Abandon(Result.Failure(again.Error!)).ConfigureAwait(false);
                if (!plannedTarget.Matches(again.Value, TimeSpan.Zero)) return await Abandon(Stale(toPath)).ConfigureAwait(false);
                if (plannedTarget.ETag is not null || plannedTarget.VersionId is not null)
                    condition = new StorageMutationCondition { ExpectedETag = plannedTarget.ETag, ExpectedVersionId = plannedTarget.VersionId };
            }
            if (cancellationToken.IsCancellationRequested)
            {
                await Abandon(Result.Success()).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            // From here the destination may be committed: nothing is cancelled half-way, and nothing rolled back.
            var promotion = await StagedWriter.PromoteCoreAsync(to, staged.StagingPath, toPath, overwrite: plannedTarget is not null, condition, createParents: true, CancellationToken.None).ConfigureAwait(false);
            var promoted = promotion.Result;
            if (promoted.IsFailure && !StorageErrorInfo.DestinationCommitted(promoted.Error))
                return await Abandon(promoted.Error!.Code == StorageErrors.ConflictCode ? Stale(toPath) : promoted).ConfigureAwait(false);
            if (promoted.IsSuccess && promotion.LeftBehind.Count > 0)
                // Committed, but the provider left its own backup or staging copy: applied, with the leftover reported.
                promoted = Result.Failure(StorageErrors.PartialFailure(
                    $"'{toPath}' was written, but the provider left internal objects behind.",
                    string.Join(';', [$"{StorageErrorInfo.DestinationStateKey}=complete", .. promotion.LeftBehind.Select(path => $"{StorageErrorInfo.LeftBehindKey}={path}")])));

            var timesSet = false;
            if (options.PreserveTimestamps && modified is not null && to.Capabilities.Supports(StorageFeature.SetTimestamps))
                // Best effort: a failure only means the next comparison relies on size and "source newer".
                timesSet = (await to.SetTimestampsAsync(toPath, modified, cancellationToken: CancellationToken.None).ConfigureAwait(false)).IsSuccess;

            // The pair the baseline records: the version read, and the version written — while it is still ours.
            var total = staged.Bytes + staged.BytesResumed;
            var readIdentity = StorageSyncIdentity.Of(fromItem)! with { Sha256 = staged.Sha256 };
            var planWritten = new StorageSyncIdentity(total, timesSet || keepTimeInMetadata ? modified : null, null, null, staged.Sha256);
            // The version written, read back: a read that keeps failing is retried, then the planned identity is
            // recorded only when it has a time to tell a later edit by (size alone would take a same-size edit for
            // ours); otherwise nothing is recorded, and the next run compares the two sides afresh.
            async Task<StorageSyncIdentity?> ReadBackAsync()
            {
                Result<StorageItem?> result = default!;
                for (var attempt = 1; attempt <= ReadBackAttempts; attempt++)
                {
                    result = await Current(to, toPath, CancellationToken.None).ConfigureAwait(false);
                    if (result.IsSuccess || !StorageErrorInfo.IsTransient(result.Error) || attempt == ReadBackAttempts) break;
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), CancellationToken.None).ConfigureAwait(false);
                }
                if (result.IsFailure) return planWritten.Modified is null ? null : planWritten;
                if (result.Value is { ItemType: not StorageItemType.Directory } item && item.Size == total &&
                    (!(timesSet || keepTimeInMetadata) || StorageCompare.EffectiveModified(item) is not { } now || (now - modified!.Value).Duration() <= tolerance))
                    return StorageSyncIdentity.Of(item);
                return null; // Replaced or removed right after the write: not ours to record.
            }

            StorageSyncIdentity? wrote;
            if (options.Verify)
            {
                var confirmed = await StagedWriter.ConfirmPromotedAsync(to, toPath, staged, CancellationToken.None).ConfigureAwait(false);
                if (confirmed.IsFailure && confirmed.Error!.Code == StorageErrors.ConflictCode)
                    // Committed, and then replaced: the next run sees the other writer's version.
                    // Not reported as committed: the step failed, as the content it wrote is no longer there.
                    return Result.Failure(StorageErrors.Conflict(
                        $"'{toPath}' was written but does not hold that content any more; another writer replaced it.",
                        string.Join(';', (confirmed.Error.Details ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries)
                            .Where(part => !part.StartsWith(StorageErrorInfo.DestinationStateKey + "=", StringComparison.Ordinal)))));
                wrote = confirmed.IsSuccess ? StorageSyncIdentity.Of(confirmed.Value) : await ReadBackAsync().ConfigureAwait(false);
            }
            else
            {
                wrote = await ReadBackAsync().ConfigureAwait(false);
            }
            if (wrote is not null)
                Synced[relative] = towardDestination ? new StorageSyncBaselineEntry(readIdentity, wrote) : new StorageSyncBaselineEntry(wrote, readIdentity);
            return promoted;
        }

        /// <summary>
        /// Deletes a file while it is exactly the planned version (no time tolerance: a same-size edit within it is
        /// still an edit), or a directory only while it is empty: anything that appeared in it after the plan keeps
        /// it, and the step is reported stale. A path whose type changed since the plan (a file where a folder was, or
        /// the other way round) is stale too.
        /// </summary>
        private static async Task<Result> DeleteAsync(IStorageService storage, string path, StorageSyncIdentity? planned, CancellationToken cancellationToken)
        {
            var read = await Current(storage, path, cancellationToken).ConfigureAwait(false);
            if (read.IsFailure) return Result.Failure(read.Error!);
            if (read.Value is not { } current) return Result.Success();
            var plannedDirectory = planned is null;
            if ((current.ItemType == StorageItemType.Directory) != plannedDirectory) return Stale(path);
            if (plannedDirectory)
            {
                var emptied = await RemoveStaleStagingAsync(storage, path, cancellationToken).ConfigureAwait(false);
                if (emptied.IsFailure) return Result.Failure(emptied.Error!);
                if (!emptied.Value) return Stale(path);
                return await storage.DeleteAsync(path, new StorageDeleteOptions { Recursive = false, IgnoreMissing = true }, cancellationToken).ConfigureAwait(false);
            }
            if (!planned!.Matches(current, TimeSpan.Zero))
                return Stale(path);
            var condition = storage.Capabilities.Supports(StorageFeature.ConditionalDelete) && (planned.ETag is not null || planned.VersionId is not null)
                ? new StorageMutationCondition { ExpectedETag = planned.ETag, ExpectedVersionId = planned.VersionId }
                : null;
            var deleted = await storage.DeleteAsync(path, new StorageDeleteOptions { Recursive = false, IgnoreMissing = true, Condition = condition }, cancellationToken).ConfigureAwait(false);
            return deleted.IsFailure && condition is not null && deleted.Error!.Code == StorageErrors.ConflictCode ? Stale(path) : deleted;
        }

        /// <summary>
        /// Whether a folder holds nothing but the library's own staging left by an interrupted transfer, removing
        /// that staging when it is older than <see cref="StaleStagingAge"/>. A folder holding anything else, a
        /// backup of a replaced file (possibly the only copy of that version), or staging a transfer may still be
        /// writing, is not empty: false.
        /// </summary>
        private static async Task<Result<bool>> RemoveStaleStagingAsync(IStorageService storage, string path, CancellationToken cancellationToken)
        {
            var staging = new List<StorageItem>();
            string? token = null;
            do
            {
                var listed = await storage.ListAsync(path, new StorageListOptions { PageSize = 100, IncludeInternal = true, IncludeHidden = true, ContinuationToken = token }, cancellationToken).ConfigureAwait(false);
                if (listed.IsFailure) return Result<bool>.Failure(listed.Error!);
                foreach (var item in listed.Value!.Items)
                {
                    if (!Providers.StorageListFilter.IsInternal(item.Name) || item.ItemType != StorageItemType.File ||
                        item.Name.Contains("backup", StringComparison.OrdinalIgnoreCase) ||
                        item.LastModified is not { } modified || DateTimeOffset.UtcNow - modified < StaleStagingAge)
                        return Result<bool>.Success(false);
                    staging.Add(item);
                }
                token = listed.Value.ContinuationToken;
            }
            while (token is not null && staging.Count < 1000);
            if (token is not null) return Result<bool>.Success(false);
            foreach (var item in staging)
            {
                var removed = await storage.DeleteAsync(item.Path, new StorageDeleteOptions { Recursive = false, IgnoreMissing = true }, cancellationToken).ConfigureAwait(false);
                if (removed.IsFailure) return Result<bool>.Failure(removed.Error!);
            }
            return Result<bool>.Success(true);
        }

        /// <summary>The item at a path, or null when it is not there; any other failure to read it is a failure.</summary>
        private static async Task<Result<StorageItem?>> Current(IStorageService storage, string path, CancellationToken cancellationToken)
        {
            var info = await storage.GetInfoAsync(path, cancellationToken).ConfigureAwait(false);
            if (info.IsSuccess) return Result<StorageItem?>.Success(info.Value);
            return info.Error!.Code == StorageErrors.NotFoundCode
                ? Result<StorageItem?>.Success(null)
                : Result<StorageItem?>.Failure(info.Error);
        }

        private static Result Stale(string path) =>
            Result.Failure(StorageErrors.Conflict($"'{path}' changed after the plan was made; the step was not taken.", $"{StaleKey}=true"));

        private static bool IsStale(Error? error) => error is not null && StorageErrorInfo.TryGetDetail(error, StaleKey, out _);
    }

    /// <summary>Details key marking a step skipped because its item changed after planning.</summary>
    private const string StaleKey = "stale";

    /// <summary>
    /// How old the library's staging left in a folder must be before a sync that deletes the folder removes it:
    /// well above any one transfer, so a transfer still writing there is never disturbed.
    /// </summary>
    internal static readonly TimeSpan StaleStagingAge = TimeSpan.FromHours(24);

    /// <summary>How often the version a copy wrote is read back before the step settles for what it knows.</summary>
    private const int ReadBackAttempts = 3;

    // ---------------------------------------------------------------- baseline

    /// <summary>
    /// Records what the run knows to be in sync: the pairs both sides agreed on when the plan was made (and the
    /// entries it carried over for paths it left alone), and the pairs the run's own steps produced. A path whose
    /// step did not complete — failed, stale, not run, withheld, a conflict — keeps its previous entry, so the next
    /// run sees it again. Nothing is taken from a listing made after the run, so an edit made meanwhile is not
    /// mistaken for the synced version. Where case is ignored, entries are matched ignoring case.
    /// </summary>
    private static async Task<(bool Saved, Error? Error)> SaveBaselineAsync(StorageSyncPlan plan, SyncRun run, StorageSyncOptions options, CancellationToken cancellationToken)
    {
        var store = options.StateStore!;
        try
        {
            var previous = await store.LoadAsync(options.SyncId!, cancellationToken).ConfigureAwait(false) ?? StorageSyncBaseline.Empty;
            var comparer = plan.CaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var before = new Dictionary<string, StorageSyncBaselineEntry>(comparer);
            foreach (var (path, entry) in previous.Entries) before.TryAdd(path, entry);
            var entries = new Dictionary<string, StorageSyncBaselineEntry>(comparer);
            foreach (var (path, entry) in plan.Agreed) entries[path] = entry;
            foreach (var result in run.Results.Where(result => result.Outcome != StorageSyncActionOutcome.Applied))
            {
                foreach (var path in new[] { result.Action.RelativePath, result.Action.TargetPath }.Where(path => path is { Length: > 0 }).Cast<string>())
                {
                    if (before.TryGetValue(path, out var kept)) entries[path] = kept;
                    else entries.Remove(path);
                }
            }
            foreach (var path in run.Removed.Keys) entries.Remove(path);
            foreach (var (path, entry) in run.Synced)
            {
                entries.Remove(path);
                entries[path] = entry;
            }
            var saved = await store.SaveAsync(options.SyncId!, new StorageSyncBaseline(previous.Generation + 1, new Dictionary<string, StorageSyncBaselineEntry>(entries, StringComparer.Ordinal)),
                plan.BaselineGeneration, cancellationToken).ConfigureAwait(false);
            return saved ? (true, null) : (false, StorageErrors.Conflict("Another run saved the baseline in between; the next run plans against it."));
        }
        catch (Exception error)
        {
            return (false, StorageErrors.FromException(error, "Save the sync baseline"));
        }
    }

    private static string Join(string root, string relative) =>
        root.Length == 0 ? relative : relative.Length == 0 ? root : $"{root}/{relative}";

    private sealed class PathProgress(IProgress<StorageTransferProgress> inner, string path) : IProgress<StorageTransferProgress>
    {
        public void Report(StorageTransferProgress value) => inner.Report(value with { ItemPath = path });
    }
}
