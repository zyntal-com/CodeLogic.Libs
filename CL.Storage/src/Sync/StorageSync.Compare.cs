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
}
