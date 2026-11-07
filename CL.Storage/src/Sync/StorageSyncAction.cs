using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Sync;

/// <summary>One planned sync step, with the versions it was planned against.</summary>
public sealed record StorageSyncAction
{
    /// <summary>Gets the path relative to the synced directories, as the source spells it.</summary>
    public required string RelativePath { get; init; }
    /// <summary>
    /// Gets the destination's spelling of the path when it differs from <see cref="RelativePath"/> only by case
    /// (one side ignores case); null when both sides spell it the same.
    /// </summary>
    public string? DestinationRelativePath { get; init; }
    /// <summary>Gets what is done.</summary>
    public required StorageSyncActionKind Kind { get; init; }
    /// <summary>Gets why, from the comparison.</summary>
    public StorageDiffReason Reason { get; init; }
    /// <summary>Gets the content size copied, when known.</summary>
    public long? Bytes { get; init; }
    /// <summary>Gets the source's version when planned; the step only runs while it is unchanged.</summary>
    public StorageSyncIdentity? Source { get; init; }
    /// <summary>Gets the destination's version when planned, or null when it was absent.</summary>
    public StorageSyncIdentity? Destination { get; init; }
    /// <summary>Gets the conflict, for <see cref="StorageSyncActionKind.Conflict"/> and steps resolving one.</summary>
    public StorageSyncConflictKind? Conflict { get; init; }
    /// <summary>Gets the other path of a rename, or the name a copy writes to.</summary>
    public string? TargetPath { get; init; }
    /// <summary>Gets why a deletion is held back, when a safety rule applies.</summary>
    public string? WithheldReason { get; init; }
    /// <summary>
    /// Gets, for a copy of an item reached through a followed link (<see cref="StorageLinkHandling.Follow"/>), the
    /// path on the connection copied from where its content is read: the link's target, or the item inside it.
    /// Null for everything else.
    /// </summary>
    public string? ReadPath { get; init; }
}
