using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Sync;

/// <summary>Controls a directory comparison.</summary>
public sealed record StorageCompareOptions
{
    /// <summary>
    /// The metadata key that keeps a file's source modification time on stores that cannot set times. The value
    /// is an ISO 8601 time; one written without an offset is read as UTC.
    /// </summary>
    public const string ModifiedMetadataKey = "cl-mtime";

    /// <summary>Gets the criteria; size and time by default, like FileZilla's directory comparison.</summary>
    public StorageCompareBy CompareBy { get; init; } = StorageCompareBy.Size | StorageCompareBy.Time;
    /// <summary>Gets the slack allowed between modification times; FAT and many FTP servers store two-second or whole-second times.</summary>
    public TimeSpan TimeTolerance { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>
    /// Gets the preferred checksum algorithm for <see cref="StorageCompareBy.Checksum"/>. Where the two sides keep
    /// different server checksums (MD5 on one, SHA-256 on the other), the one a side already has is used, so only
    /// the other side is downloaded.
    /// </summary>
    public StorageChecksumAlgorithm ChecksumAlgorithm { get; init; } = StorageChecksumAlgorithm.Md5;
    /// <summary>
    /// Gets whether hidden items (dot-files, and items the provider marks hidden) take part. A hidden folder is
    /// left out with everything inside it.
    /// </summary>
    public bool IncludeHidden { get; init; } = true;
    /// <summary>Gets an optional <c>*</c>/<c>?</c> file-name filter; directories are always walked.</summary>
    public string? NamePattern { get; init; }
    /// <summary>
    /// Gets path globs a file must match to take part (<c>**</c> spans folders, <c>*</c> and <c>?</c> stay within
    /// one name, matched against the path relative to the compared folder, case-insensitively). Empty means all.
    /// </summary>
    public IReadOnlyList<string> Include { get; init; } = [];
    /// <summary>
    /// Gets path globs to leave out entirely. A folder that matches (<c>**/node_modules</c>, <c>build/**</c>) is
    /// left out with everything inside it, as in <c>.gitignore</c>.
    /// </summary>
    public IReadOnlyList<string> Exclude { get; init; } = [];
    /// <summary>Gets how many files are hashed at once.</summary>
    public int HashConcurrency { get; init; } = 4;
    /// <summary>Gets the most files downloaded to hash in one comparison; beyond it a difference is <see cref="StorageDiffReason.Undecidable"/>.</summary>
    public int? MaxHashedFiles { get; init; }
    /// <summary>Gets the most bytes downloaded to hash in one comparison; a file of unknown size is not hashed when set.</summary>
    public long? MaxHashedBytes { get; init; }
    /// <summary>
    /// Gets whether names differing only by case are one item. Null decides from the connections
    /// (<see cref="StorageFeature.CaseInsensitivePaths"/> on either side). When true, two such names on one side
    /// fail the comparison with <c>storage.conflict</c>, and names are also matched across Unicode
    /// normalization forms (NFC and NFD).
    /// </summary>
    public bool? CaseInsensitive { get; init; }
    /// <summary>
    /// Gets how links are treated: skipped by default. A skipped link to a folder is left out with its contents.
    /// A followed link is compared as what it leads to (a link to a folder as a folder holding its target's
    /// contents), and an item reached through one has as its <see cref="StorageItem.Path"/> the path its content
    /// is read from; a link that leads outside the connection, is broken, or leads back into a folder being listed
    /// is left out like a skipped one. A sync never deletes or replaces anything through a followed link.
    /// </summary>
    public StorageLinkHandling LinkHandling { get; init; } = StorageLinkHandling.Skip;
    /// <summary>Gets the most items one side may hold; a larger tree fails instead of exhausting memory.</summary>
    public int MaxItems { get; init; } = 1_000_000;

    internal Result Validate()
    {
        if (TimeTolerance < TimeSpan.Zero) return Result.Failure(StorageErrors.InvalidContent("TimeTolerance cannot be negative."));
        if (HashConcurrency is < 1 or > 64) return Result.Failure(StorageErrors.InvalidContent("HashConcurrency must be between 1 and 64."));
        if (MaxItems < 1) return Result.Failure(StorageErrors.InvalidContent("MaxItems must be positive."));
        if (LinkHandling == StorageLinkHandling.Recreate)
            return Result.Failure(StorageErrors.InvalidContent("Comparisons and syncs cannot recreate links; use Skip, Follow, or Reject."));
        return Enum.IsDefined(LinkHandling) ? Result.Success() : Result.Failure(StorageErrors.InvalidContent("LinkHandling is invalid."));
    }
}
