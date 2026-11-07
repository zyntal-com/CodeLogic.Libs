using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Sync;

/// <summary>The result of comparing two directory trees.</summary>
/// <param name="Entries">Every compared path, sorted so that a folder comes right before its contents.</param>
public sealed record StorageDiff(IReadOnlyList<StorageDiffEntry> Entries)
{
    /// <summary>Gets whether the trees match by the chosen criteria.</summary>
    public bool Identical => Entries.All(entry => entry.Kind == StorageDiffKind.Same);

    /// <summary>Gets what the filters, hidden rule, and link handling left out, per side, for safe deletes.</summary>
    internal StorageExclusions SourceExclusions { get; init; } = new();
    internal StorageExclusions DestinationExclusions { get; init; } = new();
    /// <summary>Gets the links each side followed, so nothing is deleted or overwritten through one.</summary>
    internal StorageFollowedLinks SourceLinks { get; init; } = new();
    internal StorageFollowedLinks DestinationLinks { get; init; } = new();
    internal bool CaseInsensitive { get; init; }
}
