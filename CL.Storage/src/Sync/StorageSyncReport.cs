using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Sync;

/// <summary>The outcome of a sync, or its plan for a dry run.</summary>
public sealed record StorageSyncReport
{
    /// <summary>Gets the plan that ran.</summary>
    public required StorageSyncPlan Plan { get; init; }
    /// <summary>Gets each step's outcome, in plan order.</summary>
    public IReadOnlyList<StorageSyncActionResult> Results { get; init; } = [];
    /// <summary>Gets whether nothing was changed on purpose.</summary>
    public bool DryRun { get; init; }
    /// <summary>Gets whether the run was cancelled; the results say what was done before.</summary>
    public bool Cancelled { get; init; }
    /// <summary>Gets whether the baseline was saved after the run.</summary>
    public bool BaselineSaved { get; init; }
    /// <summary>
    /// Gets why the baseline was not saved: the store failed, or another run saved in between
    /// (<c>storage.conflict</c>). Null when it was saved or there is no baseline.
    /// </summary>
    public Error? BaselineError { get; init; }

    /// <summary>Gets the planned steps.</summary>
    public IReadOnlyList<StorageSyncAction> Actions => Plan.Actions;
    /// <summary>Gets the files that already matched.</summary>
    public int Unchanged => Plan.Unchanged;
    /// <summary>Gets the steps that failed.</summary>
    public IReadOnlyList<StorageSyncActionResult> Failed => [.. Results.Where(result => result.Outcome == StorageSyncActionOutcome.Failed)];
    /// <summary>Gets the steps skipped because their item changed after the plan was made.</summary>
    public IReadOnlyList<StorageSyncActionResult> Stale => [.. Results.Where(result => result.Outcome == StorageSyncActionOutcome.Stale)];
    /// <summary>Gets the deletions held back by a safety rule.</summary>
    public IReadOnlyList<StorageSyncActionResult> Withheld => [.. Results.Where(result => result.Outcome == StorageSyncActionOutcome.Withheld)];
    /// <summary>Gets the conflicts left for a person.</summary>
    public IReadOnlyList<StorageSyncAction> Conflicts => Plan.Conflicts;
    /// <summary>Gets the number of files copied in either direction.</summary>
    public int Copied => Results.Count(result => result.Outcome == StorageSyncActionOutcome.Applied && result.Action.Kind is StorageSyncActionKind.CopyToDestination or StorageSyncActionKind.CopyToSource);
    /// <summary>Gets the number of items deleted on either side.</summary>
    public int Deleted => Results.Count(result => result.Outcome == StorageSyncActionOutcome.Applied && result.Action.Kind is StorageSyncActionKind.DeleteFromDestination or StorageSyncActionKind.DeleteFromSource);
}
