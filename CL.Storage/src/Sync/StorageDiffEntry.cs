using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Sync;

/// <summary>One path compared on both sides.</summary>
/// <param name="RelativePath">
/// Path relative to the compared directories, as the source spells it. When case is ignored and the item exists
/// only at the destination, its folders take the source's spelling where the source has them.
/// </param>
/// <param name="Kind">How the two sides relate.</param>
/// <param name="Reasons">Why they differ, for <see cref="StorageDiffKind.Different"/>.</param>
/// <param name="Source">The source item, when it exists.</param>
/// <param name="Destination">The destination item, when it exists.</param>
public sealed record StorageDiffEntry(
    string RelativePath,
    StorageDiffKind Kind,
    StorageDiffReason Reasons,
    StorageItem? Source,
    StorageItem? Destination)
{
    /// <summary>
    /// Gets the destination's spelling of the path when it differs from <see cref="RelativePath"/> only by case
    /// (a case-insensitive comparison); null when both sides spell it the same. For an item only at the source it
    /// is where the item belongs at the destination: in the destination's spelling of any folder it already has.
    /// </summary>
    public string? DestinationRelativePath { get; init; }

    /// <summary>Gets whether this entry is a directory on either side.</summary>
    public bool IsDirectory => (Source ?? Destination)?.ItemType == StorageItemType.Directory;
}
