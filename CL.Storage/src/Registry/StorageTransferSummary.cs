using System.Buffers;
using System.IO.Pipelines;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Providers;
using CodeLogic.Core.Results;

namespace CL.Storage.Registry;

/// <param name="SourceType">Whether the transferred source was a file, directory, or link.</param>
/// <param name="Files">Files written to the destination.</param>
/// <param name="Directories">Directories created or reused at the destination.</param>
/// <param name="Bytes">Content bytes relayed.</param>
/// <param name="SkippedFiles">Files (and links) the conflict or link policy left untouched.</param>
/// <param name="TransferredSources">Source paths of the files that were written, so a move can delete only those.</param>
internal sealed record StorageTransferSummary(
    StorageItemType SourceType,
    long Files,
    long Directories,
    long Bytes,
    long SkippedFiles = 0,
    IReadOnlyList<string>? TransferredSources = null)
{
    /// <summary>The path written for a single file, after a rename.</summary>
    public string? WrittenPath { get; init; }
    /// <summary>Why a single file was skipped.</summary>
    public StorageSkipReason? SkipReason { get; init; }
    /// <summary>The content digest of a single file.</summary>
    public string? Sha256 { get; init; }
    /// <summary>How a verified single file was confirmed.</summary>
    public string? VerifiedBy { get; init; }
    /// <summary>The destination's identity after a single-file commit.</summary>
    public StorageItem? Destination { get; init; }
    /// <summary>Bytes reused from an earlier attempt's staging.</summary>
    public long BytesResumed { get; init; }
    /// <summary>How a destination condition was enforced.</summary>
    public StorageConditionEnforcement ConditionEnforcement { get; init; }
    /// <summary>
    /// The source items that were written, with the identity they had when they were listed, so a move deletes
    /// each one only while it is still that version.
    /// </summary>
    public IReadOnlyList<StorageItem>? TransferredItems { get; init; }
    /// <summary>Source paths a policy left in place on purpose (skipped files and links).</summary>
    public IReadOnlyList<string>? SkippedSources { get; init; }
    /// <summary>Directories this transfer created at the destination.</summary>
    public long CreatedDirectories { get; init; }
    /// <summary>A staging object a provider could not remove after it committed.</summary>
    public string? StagingLeftBehind { get; init; }
    /// <summary>A backup (the library's or a provider's) that could not be removed after the commit.</summary>
    public string? BackupLeftBehind { get; init; }
}
