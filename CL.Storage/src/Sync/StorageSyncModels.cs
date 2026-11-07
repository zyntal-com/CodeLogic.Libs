using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Sync;

/// <summary>Controls a directory sync.</summary>
public sealed record StorageSyncOptions
{
    /// <summary>Gets which way changes flow.</summary>
    public StorageSyncDirection Direction { get; init; } = StorageSyncDirection.Update;
    /// <summary>Gets whether <see cref="StorageSyncDirection.Mirror"/> deletes items that exist only at the destination.</summary>
    public bool DeleteExtraneous { get; init; }
    /// <summary>Gets whether a two-way sync carries deletions to the other side (only ever through the baseline).</summary>
    public bool PropagateDeletes { get; init; } = true;
    /// <summary>Gets what a two-way sync does with changes on both sides.</summary>
    public StorageSyncConflictPolicy ConflictPolicy { get; init; } = StorageSyncConflictPolicy.Block;
    /// <summary>Gets where baselines are kept; required for three-way two-way syncs.</summary>
    [JsonIgnore]
    public IStorageSyncStateStore? StateStore { get; init; }
    /// <summary>
    /// Gets the name this sync's baseline is stored under. The baseline is bound to this name only, not to the
    /// connections or directories: give each pair of directories its own name, since a baseline read against other
    /// directories takes their differences for edits and deletions (the empty-side and delete limits still apply).
    /// </summary>
    public string? SyncId { get; init; }
    /// <summary>Gets whether to only plan: nothing is changed and every action is returned as planned.</summary>
    public bool DryRun { get; init; }
    /// <summary>Gets whether copied files keep the source's modification time (set, or kept in metadata on object stores).</summary>
    public bool PreserveTimestamps { get; init; } = true;
    /// <summary>
    /// Gets whether each copy is verified: SHA-256 during the copy, the staged object confirmed before it is
    /// promoted, and the promoted file confirmed on the destination (its length, and its SHA-256 where the server
    /// keeps one). The digest is recorded in the baseline (<see cref="StorageSyncIdentity.Sha256"/>).
    /// </summary>
    public bool Verify { get; init; }
    /// <summary>Gets how many files are copied at once.</summary>
    public int MaxConcurrency { get; init; } = 4;
    /// <summary>Gets the comparison settings: criteria, filters, case rules, links, and hashing budget.</summary>
    public StorageCompareOptions Compare { get; init; } = new();
    /// <summary>Gets the most files one run may delete; beyond it every deletion is withheld.</summary>
    public int? MaxDeletes { get; init; }
    /// <summary>Gets the largest share (0–100) of a side's files one run may delete; beyond it every deletion is withheld.</summary>
    public double? MaxDeletePercent { get; init; }
    /// <summary>
    /// Gets whether deletions may run when a side is unexpectedly empty — no files where the other side (or the
    /// baseline) has them, as when a drive is not mounted or a root is wrong. Off by default.
    /// </summary>
    public bool AllowEmptySide { get; init; }
    /// <summary>Gets how often a step that failed transiently is retried in the same run.</summary>
    public int ItemRetries { get; init; } = 2;
    /// <summary>Gets whether the run continues after a step fails; otherwise the remaining steps are not run.</summary>
    public bool ContinueOnError { get; init; } = true;
    /// <summary>
    /// Gets whether a plan with blocked conflicts (<see cref="StorageSyncConflictPolicy.Block"/>) may still apply
    /// its other steps (the conflicts themselves change nothing). It is an apply-time choice, not part of the plan's
    /// options digest: a plan made without it can be shown, and its other steps applied with it.
    /// </summary>
    public bool ApplyWithConflicts { get; init; }
    /// <summary>Gets an optional progress sink; reports carry the current file.</summary>
    [JsonIgnore]
    public IProgress<StorageTransferProgress>? Progress { get; init; }

    internal Result Validate()
    {
        if (MaxConcurrency is < 1 or > 64)
            return Result.Failure(StorageErrors.InvalidContent("MaxConcurrency must be between 1 and 64."));
        if (DeleteExtraneous && Direction != StorageSyncDirection.Mirror)
            return Result.Failure(StorageErrors.InvalidContent("DeleteExtraneous only applies to Mirror syncs."));
        if (MaxDeletes is < 0 || MaxDeletePercent is { } percent && (double.IsNaN(percent) || percent is < 0 or > 100))
            return Result.Failure(StorageErrors.InvalidContent("MaxDeletes cannot be negative and MaxDeletePercent must be between 0 and 100."));
        if (ItemRetries is < 0 or > 20)
            return Result.Failure(StorageErrors.InvalidContent("ItemRetries must be between 0 and 20."));
        if ((StateStore is null) != (SyncId is null))
            return Result.Failure(StorageErrors.InvalidContent("StateStore and SyncId are set together."));
        return Compare.Validate();
    }

    /// <summary>
    /// A digest of every option that shapes what a plan does when applied — direction, deletes, conflicts,
    /// comparison and time tolerance, safety limits, verification — so a plan is applied only with the options
    /// it was made (and approved) with. What only changes how a run goes, not what it does, is left out:
    /// concurrency (copies and hashing), retries, stopping on error, progress, and <see cref="ApplyWithConflicts"/>.
    /// </summary>
    internal string Fingerprint()
    {
        var shape = new
        {
            Direction,
            DeleteExtraneous,
            PropagateDeletes,
            ConflictPolicy,
            SyncId,
            PreserveTimestamps,
            Verify,
            // How many files are hashed at once changes how fast a plan is made, not what it holds.
            Compare = Compare with { HashConcurrency = FingerprintHashConcurrency },
            MaxDeletes,
            MaxDeletePercent,
            AllowEmptySide
        };
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(shape, FingerprintJson)));
    }

    private const int FingerprintHashConcurrency = 4;

    private static readonly JsonSerializerOptions FingerprintJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
}
