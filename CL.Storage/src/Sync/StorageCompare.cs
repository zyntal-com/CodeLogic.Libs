using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Sync;

/// <summary>How two items relate after a comparison.</summary>
public enum StorageDiffKind
{
    /// <summary>Exists only on the source side.</summary>
    OnlyInSource = 0,
    /// <summary>Exists only on the destination side.</summary>
    OnlyInDestination = 1,
    /// <summary>Exists on both sides with different content, by the chosen criteria.</summary>
    Different = 2,
    /// <summary>Exists on both sides and matches, by the chosen criteria.</summary>
    Same = 3
}

/// <summary>Why two files were judged different.</summary>
[Flags]
public enum StorageDiffReason
{
    /// <summary>No difference.</summary>
    None = 0,
    /// <summary>Sizes differ.</summary>
    Size = 1,
    /// <summary>The source was modified later than the destination.</summary>
    SourceNewer = 2,
    /// <summary>The destination was modified later than the source.</summary>
    DestinationNewer = 4,
    /// <summary>Checksums differ.</summary>
    Checksum = 8,
    /// <summary>One side is a file and the other a directory.</summary>
    Type = 16,
    /// <summary>
    /// A checksum was needed but could not be had — the hashing budget ran out, or one side's file could not
    /// be read (locked, deleted after listing, no permission) — so equality is unknown.
    /// </summary>
    Undecidable = 32
}

/// <summary>What a comparison looks at.</summary>
[Flags]
public enum StorageCompareBy
{
    /// <summary>File sizes.</summary>
    Size = 1,
    /// <summary>Modification times, within <see cref="StorageCompareOptions.TimeTolerance"/>.</summary>
    Time = 2,
    /// <summary>Content checksums: the server's where it keeps one, otherwise downloaded and hashed within the budget.</summary>
    Checksum = 4
}

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

/// <summary>A listed tree: included items keyed by path, what was left out, and whether the root was missing.</summary>
internal sealed record StorageTree(Dictionary<string, StorageItem> Items, StorageExclusions Exclusions, bool Missing)
{
    /// <summary>The links followed on this side, and where the content of each item reached through one is read.</summary>
    public StorageFollowedLinks Links { get; init; } = new();
}

/// <summary>
/// The links one side's listing followed (<see cref="StorageLinkHandling.Follow"/>): each link's path, whether it
/// leads to a folder, and for every item reached through a link the path its content is really read from.
/// </summary>
internal sealed class StorageFollowedLinks
{
    // Links compare ignoring case, which only ever leaves more alone; read paths are exact spellings.
    private readonly Dictionary<string, bool> _links = [with(StringComparer.OrdinalIgnoreCase)];
    private readonly Dictionary<string, string> _readPaths = [with(StringComparer.Ordinal)];

    /// <summary>Records a followed link.</summary>
    public void Link(string relative, bool toDirectory) => _links[relative] = toDirectory;

    /// <summary>Records where an item reached through a link is read from.</summary>
    public void ReadFrom(string relative, string path) => _readPaths[relative] = path;

    /// <summary>The path an item's content is read from, when it was reached through a link.</summary>
    public string? ReadPath(string relative) => _readPaths.GetValueOrDefault(relative);

    /// <summary>Whether the path is a followed link to a file.</summary>
    public bool IsFileLink(string relative) => _links.TryGetValue(relative, out var toDirectory) && !toDirectory;

    /// <summary>Whether the path is a followed link, or lies below a followed link to a folder.</summary>
    public bool Through(string relative)
    {
        if (_links.Count == 0) return false;
        if (_links.ContainsKey(relative)) return true;
        for (var slash = relative.IndexOf('/'); slash > 0; slash = relative.IndexOf('/', slash + 1))
        {
            if (_links.TryGetValue(relative[..slash], out var toDirectory) && toDirectory) return true;
        }
        return false;
    }
}

/// <summary>
/// What one side's listing left out, kept small: folders left out with their contents are kept as prefixes, items
/// left out by a path rule (the same on both sides) only mark their folder, and only items left out for what they
/// are on this side (a link, an item the provider marks hidden) are kept one by one. Paths compare ignoring case,
/// which only ever keeps more.
/// </summary>
internal sealed class StorageExclusions
{
    private readonly HashSet<string> _pruned = [with(StringComparer.OrdinalIgnoreCase)];
    private readonly HashSet<string> _items = [with(StringComparer.OrdinalIgnoreCase)];
    private readonly HashSet<string> _keeping = [with(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Gets how many paths are kept, for the item limit.</summary>
    public int Count => _pruned.Count + _items.Count;

    /// <summary>Leaves out a folder (or a link to one) and everything below it.</summary>
    public void Prune(string path)
    {
        _pruned.Add(path);
        Keep(Parent(path));
    }

    /// <summary>Leaves out one item for what it is on this side, so the other side's copy is left alone too.</summary>
    public void Item(string path)
    {
        _items.Add(path);
        Keep(Parent(path));
    }

    /// <summary>Records that a folder holds an item a path rule left out.</summary>
    public void Holds(string path) => Keep(Parent(path));

    /// <summary>Whether a path lies below a folder that was left out.</summary>
    public bool UnderPruned(string path)
    {
        if (_pruned.Count == 0) return false;
        for (var slash = path.IndexOf('/'); slash > 0; slash = path.IndexOf('/', slash + 1))
        {
            if (_pruned.Contains(path[..slash])) return true;
        }
        return false;
    }

    /// <summary>Whether this side left the path out: itself, or a folder above it.</summary>
    public bool Covers(string path) => _items.Contains(path) || _pruned.Contains(path) || UnderPruned(path);

    /// <summary>Whether a folder (or one below it) holds something that was left out, so it must not be deleted.</summary>
    public bool Keeps(string folder) => _keeping.Contains(folder);

    private void Keep(string folder)
    {
        while (_keeping.Add(folder) && folder.Length > 0)
            folder = Parent(folder);
    }

    internal static string Parent(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }
}

/// <summary>Compares directory trees across any two storage connections.</summary>
public static class StorageCompare
{
    /// <summary>Compares two directory trees recursively.</summary>
    /// <param name="source">Source connection.</param>
    /// <param name="sourcePath">Source directory.</param>
    /// <param name="destination">Destination connection; may be the same as <paramref name="source"/>.</param>
    /// <param name="destinationPath">Destination directory; a missing one compares as empty.</param>
    /// <param name="options">Comparison criteria, filters, and budgets.</param>
    /// <param name="cancellationToken">Token used to cancel the listings and checksums.</param>
    /// <returns>Every path on either side with its relation.</returns>
    public static async Task<Result<StorageDiff>> CompareAsync(
        IStorageService source,
        string sourcePath,
        IStorageService destination,
        string destinationPath,
        StorageCompareOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        options ??= new StorageCompareOptions();
        var valid = options.Validate();
        if (valid.IsFailure) return Result<StorageDiff>.Failure(valid.Error!);
        try
        {
            var filter = new PathFilter(options);
            var sourceTree = await ListTreeAsync(source, sourcePath, options, filter, required: true, cancellationToken).ConfigureAwait(false);
            if (sourceTree.IsFailure) return Result<StorageDiff>.Failure(sourceTree.Error!);
            var destinationTree = await ListTreeAsync(destination, destinationPath, options, filter, required: false, cancellationToken).ConfigureAwait(false);
            if (destinationTree.IsFailure) return Result<StorageDiff>.Failure(destinationTree.Error!);
            return await DiffAsync(source, sourceTree.Value!, destination, destinationTree.Value!, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Result<StorageDiff>.Failure(PlanningError(error, "Compare the directories"));
        }
    }

    /// <summary>
    /// A failure for an exception thrown while listing or comparing: a filter pattern that took too long is the
    /// caller's to fix, anything else (a provider whose listing threw) is reported as the provider's error.
    /// </summary>
    internal static Error PlanningError(Exception error, string operation) =>
        error is RegexMatchTimeoutException timeout
            ? StorageErrors.InvalidContent($"The filter pattern '{timeout.Pattern}' took too long to match '{timeout.Input}'; simplify it.")
            : StorageErrors.FromException(error, operation);

    internal static async Task<Result<StorageDiff>> DiffAsync(
        IStorageService source,
        StorageTree sourceTree,
        IStorageService destination,
        StorageTree destinationTree,
        StorageCompareOptions options,
        CancellationToken cancellationToken)
    {
        var insensitive = options.CaseInsensitive ??
            (source.Capabilities.Supports(StorageFeature.CaseInsensitivePaths) || destination.Capabilities.Supports(StorageFeature.CaseInsensitivePaths));
        var left = Key(sourceTree.Items, insensitive, "source");
        if (left.IsFailure) return Result<StorageDiff>.Failure(left.Error!);
        var right = Key(destinationTree.Items, insensitive, "destination");
        if (right.IsFailure) return Result<StorageDiff>.Failure(right.Error!);

        // Where case is ignored, a folder may be spelled differently on each side ("Docs" and "docs"). An item
        // only one side has goes under the other side's spelling of its folders, so a copy lands in the existing
        // folder instead of making a second one beside it on a store that keeps case.
        var sourceFolders = insensitive ? FolderSpellings(sourceTree.Items) : null;
        var destinationFolders = insensitive ? FolderSpellings(destinationTree.Items) : null;

        var entries = new List<StorageDiffEntry>();
        var pairs = new List<(string Path, StorageItem Left, StorageItem Right)>();
        var spelling = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in left.Value!.Keys.Union(right.Value!.Keys, StringComparer.Ordinal))
        {
            left.Value.TryGetValue(key, out var a);
            right.Value.TryGetValue(key, out var b);
            if (a is null)
            {
                var path = Respell(b!.Relative, sourceFolders);
                entries.Add(new StorageDiffEntry(path, StorageDiffKind.OnlyInDestination, StorageDiffReason.None, null, b.Item)
                {
                    DestinationRelativePath = string.Equals(path, b.Relative, StringComparison.Ordinal) ? null : b.Relative
                });
                continue;
            }
            if (b is null)
            {
                var there = Respell(a.Relative, destinationFolders);
                entries.Add(new StorageDiffEntry(a.Relative, StorageDiffKind.OnlyInSource, StorageDiffReason.None, a.Item, null)
                {
                    DestinationRelativePath = string.Equals(there, a.Relative, StringComparison.Ordinal) ? null : there
                });
                continue;
            }
            pairs.Add((a.Relative, a.Item, b.Item));
            if (!string.Equals(a.Relative, b.Relative, StringComparison.Ordinal)) spelling[a.Relative] = b.Relative;
        }

        var reasons = await CompareFilesAsync(source, destination, pairs, options, cancellationToken).ConfigureAwait(false);
        if (reasons.IsFailure) return Result<StorageDiff>.Failure(reasons.Error!);
        foreach (var (path, a, b) in pairs)
        {
            var reason = reasons.Value![path];
            entries.Add(new StorageDiffEntry(path, reason == StorageDiffReason.None ? StorageDiffKind.Same : StorageDiffKind.Different, reason, a, b)
            {
                DestinationRelativePath = spelling.GetValueOrDefault(path)
            });
        }
        entries.Sort((x, y) => ComparePaths(x.RelativePath, y.RelativePath));
        return Result<StorageDiff>.Success(new StorageDiff(entries)
        {
            SourceExclusions = sourceTree.Exclusions,
            DestinationExclusions = destinationTree.Exclusions,
            SourceLinks = sourceTree.Links,
            DestinationLinks = destinationTree.Links,
            CaseInsensitive = insensitive
        });
    }

    /// <summary>Orders paths so a folder comes right before everything inside it ("a", "a/b", "a-b").</summary>
    internal static int ComparePaths(string x, string y)
    {
        var length = Math.Min(x.Length, y.Length);
        for (var i = 0; i < length; i++)
        {
            if (x[i] == y[i]) continue;
            if (x[i] == '/') return -1;
            if (y[i] == '/') return 1;
            return x[i].CompareTo(y[i]);
        }
        return x.Length.CompareTo(y.Length);
    }

    /// <summary>
    /// Every folder on one side, by its case-insensitive key, as that side spells it: each folder listed (an empty
    /// one, or one holding only items left out, included) and each folder above a listed item.
    /// </summary>
    private static Dictionary<string, string> FolderSpellings(Dictionary<string, StorageItem> items)
    {
        var folders = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, item) in items)
        {
            // Walking up stops at the first folder already known: its own parents were added with it.
            var start = item.ItemType == StorageItemType.Directory ? path : StorageExclusions.Parent(path);
            for (var folder = start; folder.Length > 0; folder = StorageExclusions.Parent(folder))
            {
                if (!folders.TryAdd(KeyOf(folder, insensitive: true), folder)) break;
            }
        }
        return folders;
    }

    /// <summary>Spells a path's folders the way the other side does, for the deepest folder it already has.</summary>
    private static string Respell(string path, Dictionary<string, string>? folders)
    {
        if (folders is null) return path;
        for (var slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1))
        {
            if (folders.TryGetValue(KeyOf(path[..slash], insensitive: true), out var spelled))
                return spelled + path[slash..];
        }
        return path;
    }

    /// <summary>
    /// The effective modification time: the source time kept in metadata on stores that cannot set times, read
    /// as UTC when it carries no offset; otherwise the item's own time.
    /// </summary>
    internal static DateTimeOffset? EffectiveModified(StorageItem item)
    {
        if (!item.Metadata.TryGetValue(StorageCompareOptions.ModifiedMetadataKey, out var kept) ||
            !DateTimeOffset.TryParse(kept, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            return item.LastModified;
        return parsed;
    }

    private sealed record Keyed(string Relative, StorageItem Item);

    /// <summary>The key a path is matched by: itself, or upper-cased in composed (NFC) form where case is ignored.</summary>
    internal static string KeyOf(string path, bool insensitive) =>
        insensitive ? path.Normalize(NormalizationForm.FormC).ToUpperInvariant() : path;

    private static Result<Dictionary<string, Keyed>> Key(Dictionary<string, StorageItem> items, bool insensitive, string side)
    {
        var keyed = new Dictionary<string, Keyed>(StringComparer.Ordinal);
        foreach (var (relative, item) in items)
        {
            var key = KeyOf(relative, insensitive);
            if (keyed.TryGetValue(key, out var existing))
                return Result<Dictionary<string, Keyed>>.Failure(StorageErrors.Conflict(
                    $"The {side} has '{existing.Relative}' and '{relative}', which are the same name where case is ignored.",
                    $"caseCollision={existing.Relative}|{relative}"));
            keyed[key] = new Keyed(relative, item);
        }
        return Result<Dictionary<string, Keyed>>.Success(keyed);
    }

    /// <summary>Decides every file pair; object-store times and checksums are resolved in parallel and on a budget.</summary>
    private static async Task<Result<Dictionary<string, StorageDiffReason>>> CompareFilesAsync(
        IStorageService source,
        IStorageService destination,
        List<(string Path, StorageItem Left, StorageItem Right)> pairs,
        StorageCompareOptions options,
        CancellationToken cancellationToken)
    {
        var reasons = new Dictionary<string, StorageDiffReason>(StringComparer.Ordinal);
        var needsMetadata = new List<int>();
        for (var i = 0; i < pairs.Count; i++)
        {
            var (path, a, b) = pairs[i];
            reasons[path] = Basic(a, b, options);
            // Stores that cannot set times keep the source's time in metadata, which listings often omit.
            if ((reasons[path] & (StorageDiffReason.SourceNewer | StorageDiffReason.DestinationNewer)) != 0 &&
                (reasons[path] & StorageDiffReason.Size) == 0 &&
                (NeedsMetadata(source, a) || NeedsMetadata(destination, b)))
                needsMetadata.Add(i);
        }
        await Parallel.ForEachAsync(needsMetadata, new ParallelOptions { MaxDegreeOfParallelism = options.HashConcurrency, CancellationToken = cancellationToken }, async (i, token) =>
        {
            var (path, a, b) = pairs[i];
            if (NeedsMetadata(source, a) && await source.GetInfoAsync(a.Path, token).ConfigureAwait(false) is { IsSuccess: true } full) a = full.Value!;
            if (NeedsMetadata(destination, b) && await destination.GetInfoAsync(b.Path, token).ConfigureAwait(false) is { IsSuccess: true } other) b = other.Value!;
            lock (reasons) reasons[path] = Basic(a, b, options);
            lock (pairs) pairs[i] = (path, a, b);
        }).ConfigureAwait(false);

        if (!options.CompareBy.HasFlag(StorageCompareBy.Checksum))
            return Result<Dictionary<string, StorageDiffReason>>.Success(reasons);

        var candidates = pairs.Where(pair => pair.Left.ItemType == StorageItemType.File && pair.Right.ItemType == StorageItemType.File &&
            (reasons[pair.Path] & (StorageDiffReason.Size | StorageDiffReason.Type)) == 0).ToList();
        var budget = new HashBudget(options.MaxHashedFiles, options.MaxHashedBytes);
        Error? failure = null;
        // A failure that concerns the connection stops the other pairs at once; one unreadable file does not.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = options.HashConcurrency, CancellationToken = stop.Token }, async (pair, token) =>
            {
                var equal = await SameContentAsync(source, pair.Left, destination, pair.Right, options, budget, token).ConfigureAwait(false);
                lock (reasons)
                {
                    if (equal.IsFailure)
                    {
                        failure ??= equal.Error;
                        stop.Cancel();
                        return;
                    }
                    var current = reasons[pair.Path];
                    reasons[pair.Path] = equal.Value switch
                    {
                        null => current | StorageDiffReason.Undecidable,
                        false => current | StorageDiffReason.Checksum,
                        // Equal content wins over differing times: a checksum match means the file needs no copy.
                        true => current & ~(StorageDiffReason.SourceNewer | StorageDiffReason.DestinationNewer)
                    };
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (failure is not null && !cancellationToken.IsCancellationRequested)
        {
        }
        return failure is null
            ? Result<Dictionary<string, StorageDiffReason>>.Success(reasons)
            : Result<Dictionary<string, StorageDiffReason>>.Failure(failure);
    }

    private static bool NeedsMetadata(IStorageService storage, StorageItem item) =>
        item.ItemType == StorageItemType.File &&
        storage.Capabilities.Supports(StorageFeature.MetadataRead) &&
        !storage.Capabilities.Supports(StorageFeature.SetTimestamps) &&
        !item.Metadata.ContainsKey(StorageCompareOptions.ModifiedMetadataKey);

    internal static StorageDiffReason Basic(StorageItem left, StorageItem right, StorageCompareOptions options)
    {
        if (left.ItemType != right.ItemType)
            return StorageDiffReason.Type;
        if (left.ItemType is StorageItemType.Directory)
            return StorageDiffReason.None;
        if (left.ItemType is StorageItemType.Link)
            return string.Equals(left.LinkTarget, right.LinkTarget, StringComparison.Ordinal) ? StorageDiffReason.None : StorageDiffReason.Checksum;
        var reasons = StorageDiffReason.None;
        if (options.CompareBy.HasFlag(StorageCompareBy.Size) && left.Size != right.Size)
            reasons |= StorageDiffReason.Size;
        if (options.CompareBy.HasFlag(StorageCompareBy.Time) && EffectiveModified(left) is { } l && EffectiveModified(right) is { } r)
        {
            // A coarse time (an FTP listing gives minutes) stands for the interval it was truncated from.
            var lHigh = l + (left.ModifiedPrecision ?? TimeSpan.Zero);
            var rHigh = r + (right.ModifiedPrecision ?? TimeSpan.Zero);
            if (l - rHigh > options.TimeTolerance) reasons |= StorageDiffReason.SourceNewer;
            else if (r - lHigh > options.TimeTolerance) reasons |= StorageDiffReason.DestinationNewer;
        }
        return reasons;
    }

    /// <summary>
    /// Whether two files hold the same content: by server checksums both sides keep in one algorithm; else by
    /// hashing only the side without one in the algorithm the other keeps; else by hashing both. Null when the
    /// budget does not allow it or a file cannot be read; a failure only for errors about the connection itself.
    /// </summary>
    private static async Task<Result<bool?>> SameContentAsync(
        IStorageService source,
        StorageItem left,
        IStorageService destination,
        StorageItem right,
        StorageCompareOptions options,
        HashBudget budget,
        CancellationToken cancellationToken)
    {
        var preferred = options.ChecksumAlgorithm;
        StorageChecksumAlgorithm[] algorithms = preferred == StorageChecksumAlgorithm.Md5
            ? [StorageChecksumAlgorithm.Md5, StorageChecksumAlgorithm.Sha256]
            : preferred == StorageChecksumAlgorithm.Sha256
                ? [StorageChecksumAlgorithm.Sha256, StorageChecksumAlgorithm.Md5]
                : [preferred, StorageChecksumAlgorithm.Md5, StorageChecksumAlgorithm.Sha256];
        var leftKept = new Dictionary<StorageChecksumAlgorithm, string>();
        var rightKept = new Dictionary<StorageChecksumAlgorithm, string>();
        foreach (var algorithm in algorithms)
        {
            var a = await ServerChecksumAsync(source, left, algorithm, cancellationToken).ConfigureAwait(false);
            if (a.Fatal is not null) return Result<bool?>.Failure(a.Fatal);
            if (a.Unreadable) return Result<bool?>.Success(null);
            var b = await ServerChecksumAsync(destination, right, algorithm, cancellationToken).ConfigureAwait(false);
            if (b.Fatal is not null) return Result<bool?>.Failure(b.Fatal);
            if (b.Unreadable) return Result<bool?>.Success(null);
            if (a.Value is not null && b.Value is not null)
                return Result<bool?>.Success(string.Equals(a.Value, b.Value, StringComparison.OrdinalIgnoreCase));
            if (a.Value is not null) leftKept[algorithm] = a.Value;
            if (b.Value is not null) rightKept[algorithm] = b.Value;
        }

        // Only one side keeps a checksum: hash the other side in that algorithm.
        foreach (var algorithm in algorithms)
        {
            if (leftKept.TryGetValue(algorithm, out var kept))
                return await CompareComputedAsync(destination, right, algorithm, kept, budget, cancellationToken).ConfigureAwait(false);
            if (rightKept.TryGetValue(algorithm, out kept))
                return await CompareComputedAsync(source, left, algorithm, kept, budget, cancellationToken).ConfigureAwait(false);
        }

        // Neither side keeps one: both are hashed, and the budget is taken for both at once or not at all.
        if (!budget.TryTake(2, [left.Size, right.Size])) return Result<bool?>.Success(null);
        var leftHash = await ComputeAsync(source, left, preferred, cancellationToken).ConfigureAwait(false);
        if (leftHash.Fatal is not null) return Result<bool?>.Failure(leftHash.Fatal);
        if (leftHash.Value is null) return Result<bool?>.Success(null);
        var rightHash = await ComputeAsync(destination, right, preferred, cancellationToken).ConfigureAwait(false);
        if (rightHash.Fatal is not null) return Result<bool?>.Failure(rightHash.Fatal);
        if (rightHash.Value is null) return Result<bool?>.Success(null);
        return Result<bool?>.Success(string.Equals(leftHash.Value, rightHash.Value, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<Result<bool?>> CompareComputedAsync(
        IStorageService storage,
        StorageItem item,
        StorageChecksumAlgorithm algorithm,
        string expected,
        HashBudget budget,
        CancellationToken cancellationToken)
    {
        if (!budget.TryTake(1, [item.Size])) return Result<bool?>.Success(null);
        var computed = await ComputeAsync(storage, item, algorithm, cancellationToken).ConfigureAwait(false);
        if (computed.Fatal is not null) return Result<bool?>.Failure(computed.Fatal);
        return Result<bool?>.Success(computed.Value is null ? null : string.Equals(computed.Value, expected, StringComparison.OrdinalIgnoreCase));
    }

    private readonly record struct Digest(string? Value, bool Unreadable, Error? Fatal);

    /// <summary>A server checksum; a missing algorithm is no value, a file that cannot be read is unreadable.</summary>
    private static async Task<Digest> ServerChecksumAsync(IStorageService storage, StorageItem item, StorageChecksumAlgorithm algorithm, CancellationToken cancellationToken)
    {
        var server = await storage.GetServerChecksumAsync(item.Path, algorithm, cancellationToken).ConfigureAwait(false);
        if (server.IsSuccess) return new Digest(server.Value!.HexValue, false, null);
        if (server.Error!.Code == StorageErrors.UnsupportedCode) return new Digest(null, false, null);
        return IsAboutConnection(server.Error) ? new Digest(null, false, server.Error) : new Digest(null, true, null);
    }

    private static async Task<Digest> ComputeAsync(IStorageService storage, StorageItem item, StorageChecksumAlgorithm algorithm, CancellationToken cancellationToken)
    {
        var computed = await storage.ComputeChecksumAsync(item.Path, algorithm, mode: StorageChecksumMode.ComputeOnly, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (computed.IsSuccess) return new Digest(computed.Value!.HexValue, false, null);
        return IsAboutConnection(computed.Error!) ? new Digest(null, false, computed.Error) : new Digest(null, true, null);
    }

    /// <summary>
    /// Whether an error concerns the connection rather than one file: the comparison fails for those, while a
    /// locked, vanished, or forbidden file only leaves its own pair undecided.
    /// </summary>
    private static bool IsAboutConnection(Error error) =>
        StorageErrorInfo.IsTransient(error) || error.Code is StorageErrors.UnauthorizedCode or StorageErrors.AuthenticationFailedCode
            or StorageErrors.TlsFailureCode or StorageErrors.HostKeyRejectedCode or StorageErrors.CancelledCode;

    private sealed class HashBudget(int? files, long? bytes)
    {
        private readonly Lock _gate = new();
        private int _files;
        private long _bytes;

        /// <summary>Takes room for <paramref name="count"/> files at once, or none; a file of unknown size fits no byte budget.</summary>
        public bool TryTake(int count, long?[] sizes)
        {
            lock (_gate)
            {
                if (files is { } maxFiles && _files + count > maxFiles) return false;
                if (bytes is { } maxBytes)
                {
                    if (sizes.Any(size => size is null)) return false;
                    var total = sizes.Sum(size => size!.Value);
                    if (_bytes + total > maxBytes) return false;
                    _bytes += total;
                }
                _files += count;
                return true;
            }
        }
    }

    /// <summary>
    /// Lists a tree once, applying the hidden rule, name/include/exclude filters, and link handling. A folder the
    /// hidden rule or an exclude pattern leaves out is left out with everything below it, and a skipped link to a
    /// folder with its contents; what was left out is recorded so deletes never touch it on the other side. A
    /// followed link to a folder is listed as a folder holding its target's contents (links inside it followed
    /// the same way, up to <see cref="MaxLinkDepth"/> deep); one that leads back into a folder being listed is
    /// left out with everything below it. An item reached through a link keeps, as its
    /// <see cref="StorageItem.Path"/>, the path its content is read from.
    /// </summary>
    internal static async Task<Result<StorageTree>> ListTreeAsync(
        IStorageService storage,
        string root,
        StorageCompareOptions options,
        PathFilter filter,
        bool required,
        CancellationToken cancellationToken)
    {
        var items = new Dictionary<string, StorageItem>(StringComparer.Ordinal);
        var exclusions = new StorageExclusions();
        var links = new StorageFollowedLinks();
        var normalizedRoot = StoragePath.Normalize(root);
        if (normalizedRoot.IsFailure) return Result<StorageTree>.Failure(normalizedRoot.Error!);
        // Hidden items are listed and left out here, so they are recorded (and a hidden folder's contents go too).
        var listOptions = new StorageListOptions { Recursive = true, IncludeHidden = true };
        var pattern = string.IsNullOrEmpty(options.NamePattern) ? null : Providers.StorageListFilter.Glob(options.NamePattern);
        // Followed links to folders, whose contents come from their targets: whatever a provider itself lists below
        // one is not taken a second time.
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        var missing = false;
        var listed = 0;

        bool UnderExpanded(string relative)
        {
            if (expanded.Count == 0) return false;
            for (var slash = relative.IndexOf('/'); slash > 0; slash = relative.IndexOf('/', slash + 1))
            {
                if (expanded.Contains(relative[..slash])) return true;
            }
            return false;
        }

        Error? TooMany() => items.Count + exclusions.Count > options.MaxItems
            ? StorageErrors.TooLarge($"'{normalizedRoot.Value}' holds more than {options.MaxItems} items; raise MaxItems or narrow the filters.")
            : null;

        void LeaveOutLink(string relative)
        {
            exclusions.Item(relative);
            exclusions.Prune(relative);
        }

        // Takes one listed item in, or leaves it out; an error ends the listing.
        async Task<Error?> AdmitAsync(StorageItem current, string relative, bool reached, IReadOnlyList<string> chain)
        {
            var isDirectory = current.ItemType == StorageItemType.Directory;
            if (!options.IncludeHidden && (current.IsHidden || current.Name.StartsWith('.')))
            {
                if (isDirectory || current.ItemType == StorageItemType.Link) exclusions.Prune(relative);
                else if (current.Name.StartsWith('.')) exclusions.Holds(relative);
                else exclusions.Item(relative);
                return null;
            }
            // A followed link is filtered as what it leads to: a link to a folder is a folder to the filters.
            StorageItem? target = null;
            if (current.ItemType == StorageItemType.Link && options.LinkHandling == StorageLinkHandling.Follow)
            {
                target = await FollowAsync(storage, current, cancellationToken).ConfigureAwait(false);
                if (target is null) { LeaveOutLink(relative); return null; }
                isDirectory = target.ItemType == StorageItemType.Directory;
            }
            if (filter.Excludes(relative, isDirectory))
            {
                if (isDirectory) exclusions.Prune(relative);
                else exclusions.Holds(relative);
                return null;
            }
            if (pattern is not null && (target ?? current).ItemType == StorageItemType.File && !pattern.IsMatch(current.Name))
            {
                exclusions.Holds(relative);
                return null;
            }
            if (current.ItemType != StorageItemType.Link)
            {
                if (reached) links.ReadFrom(relative, current.Path);
                items[relative] = current;
                return TooMany();
            }
            switch (options.LinkHandling)
            {
                case StorageLinkHandling.Reject:
                    return StorageErrors.Unsupported($"'{current.Path}' is a link. Set LinkHandling to skip, follow, or recreate links.");
                case StorageLinkHandling.Follow:
                    var toDirectory = isDirectory;
                    if (toDirectory && (LeadsBack(target!.Path, current.Path, chain) || chain.Count > MaxLinkDepth))
                    {
                        // Following it would list a folder already being listed, or go too deep.
                        LeaveOutLink(relative);
                        return null;
                    }
                    links.Link(relative, toDirectory);
                    links.ReadFrom(relative, target!.Path);
                    items[relative] = target with { Name = current.Name };
                    if (TooMany() is { } tooMany) return tooMany;
                    if (!toDirectory) return null;
                    expanded.Add(relative);
                    return await ExpandAsync(target.Path, relative, [.. chain, target.Path]).ConfigureAwait(false);
                default:
                    // Skipped, with anything a provider lists below a link to a folder.
                    LeaveOutLink(relative);
                    return null;
            }
        }

        // Lists a followed folder's target and takes its contents in under the link's path.
        async Task<Error?> ExpandAsync(string targetPath, string linkRelative, IReadOnlyList<string> chain)
        {
            await foreach (var item in storage.EnumerateItemsAsync(targetPath, listOptions, cancellationToken).ConfigureAwait(false))
            {
                if (item.IsFailure) return item.Error!;
                var below = Relative(targetPath, item.Value!.Path);
                if (below is null)
                    return StorageErrors.ProviderError($"Listing '{targetPath}' returned '{item.Value.Path}', which is not inside it.", $"listedPath={item.Value.Path}");
                if (below.Length == 0) continue;
                var relative = $"{linkRelative}/{below}";
                if (exclusions.UnderPruned(relative)) continue;
                var admitted = await AdmitAsync(item.Value, relative, reached: true, chain).ConfigureAwait(false);
                if (admitted is not null) return admitted;
            }
            return null;
        }

        await foreach (var item in storage.EnumerateItemsAsync(normalizedRoot.Value!, listOptions, cancellationToken).ConfigureAwait(false))
        {
            if (item.IsFailure)
            {
                // A missing destination is empty; any other listing failure makes the tree unusable.
                if (!required && item.Error!.Code == StorageErrors.NotFoundCode && listed == 0) { missing = true; break; }
                return Result<StorageTree>.Failure(item.Error!);
            }
            listed++;
            var relative = Relative(normalizedRoot.Value!, item.Value!.Path);
            // An item outside the listed folder would otherwise vanish from the comparison, and look deleted.
            if (relative is null)
                return Result<StorageTree>.Failure(StorageErrors.ProviderError(
                    $"Listing '{normalizedRoot.Value}' returned '{item.Value.Path}', which is not inside it.",
                    $"listedPath={item.Value.Path}"));
            if (relative.Length == 0) continue;
            if (exclusions.UnderPruned(relative) || UnderExpanded(relative)) continue;
            var failed = await AdmitAsync(item.Value, relative, reached: false, [normalizedRoot.Value!]).ConfigureAwait(false);
            if (failed is not null) return Result<StorageTree>.Failure(failed);
        }
        return Result<StorageTree>.Success(new StorageTree(items, exclusions, missing) { Links = links });
    }

    /// <summary>How many followed links to folders may lie inside one another.</summary>
    internal const int MaxLinkDepth = 8;

    /// <summary>
    /// Whether following a link to a folder would list a folder already being listed: the target holds the link
    /// itself, or holds (or is) the listed root or the target of a link the new one was found through.
    /// </summary>
    private static bool LeadsBack(string target, string linkPath, IReadOnlyList<string> chain)
    {
        bool Holds(string path) =>
            target.Length == 0 || string.Equals(path, target, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(target + "/", StringComparison.OrdinalIgnoreCase);
        return Holds(linkPath) || chain.Any(Holds);
    }

    /// <summary>
    /// What a link points to, as a file or folder of the same connection: the provider's own answer when it
    /// follows links, otherwise the link's target resolved against its folder (or the connection's root for an
    /// absolute target inside it). Null when the target is elsewhere, missing, or a chain of links too long.
    /// </summary>
    private static async Task<StorageItem?> FollowAsync(IStorageService storage, StorageItem link, CancellationToken cancellationToken)
    {
        var current = link;
        for (var hop = 0; hop < 8; hop++)
        {
            var info = await storage.GetInfoAsync(current.Path, cancellationToken).ConfigureAwait(false);
            if (info.IsSuccess && info.Value!.ItemType is StorageItemType.File or StorageItemType.Directory)
                return info.Value;
            var linkTarget = (info.IsSuccess ? info.Value!.LinkTarget : null) ?? current.LinkTarget;
            if (linkTarget is null || ResolveLinkTarget(storage.Root, current.Path, linkTarget) is not { } targetPath)
                return null;
            current = new StorageItem { Path = targetPath, Name = targetPath[(targetPath.LastIndexOf('/') + 1)..], ItemType = StorageItemType.Link };
        }
        return null;
    }

    /// <summary>A link target as a path of the connection, or null when it points outside it.</summary>
    internal static string? ResolveLinkTarget(string root, string linkPath, string target)
    {
        var text = target.Replace('\\', '/');
        var rooted = text.StartsWith('/') || (text.Length > 1 && text[1] == ':');
        string combined;
        if (rooted)
        {
            var normalizedRoot = root.Replace('\\', '/').TrimEnd('/');
            if (normalizedRoot.Length == 0 || !text.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase)) return null;
            combined = text[(normalizedRoot.Length + 1)..];
        }
        else
        {
            combined = $"{StorageExclusions.Parent(linkPath)}/{text}";
        }
        var parts = new List<string>();
        foreach (var part in combined.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) return null;
                parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(part);
        }
        return parts.Count == 0 ? null : string.Join('/', parts);
    }

    /// <summary>
    /// A listed path relative to the listed folder. A provider that answers in the server's spelling of the
    /// folder ("Docs" for a request of "docs") is matched ignoring case; null for a path outside the folder.
    /// </summary>
    internal static string? Relative(string root, string path)
    {
        if (root.Length == 0) return path;
        if (string.Equals(root, path, StringComparison.OrdinalIgnoreCase)) return string.Empty;
        return path.Length > root.Length && path[root.Length] == '/' && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? path[(root.Length + 1)..]
            : null;
    }
}

/// <summary>Include and exclude globs over relative paths: <c>**</c> spans folders, <c>*</c> and <c>?</c> stay in one name.</summary>
internal sealed class PathFilter
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);
    private readonly Regex[] _include;
    private readonly Regex[] _exclude;

    public PathFilter(StorageCompareOptions options)
    {
        _include = [.. options.Include.Select(Compile)];
        _exclude = [.. options.Exclude.Select(Compile)];
    }

    /// <summary>Whether a path is left out; a folder matching an exclude pattern is left out with its contents.</summary>
    public bool Excludes(string relativePath, bool isDirectory)
    {
        if (_exclude.Any(glob => glob.IsMatch(relativePath) || (isDirectory && glob.IsMatch(relativePath + "/"))))
            return true;
        return !isDirectory && _include.Length > 0 && !_include.Any(glob => glob.IsMatch(relativePath));
    }

    internal static Regex Compile(string glob)
    {
        var pattern = new StringBuilder("^");
        var text = glob.Replace('\\', '/').TrimStart('/');
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var slashAfter = i + 2 < text.Length && text[i + 2] == '/';
                pattern.Append(slashAfter ? "(?:.*/)?" : ".*");
                i += slashAfter ? 2 : 1;
            }
            else if (c == '*') pattern.Append("[^/]*");
            else if (c == '?') pattern.Append("[^/]");
            else pattern.Append(Regex.Escape(c.ToString()));
        }
        return new Regex(pattern.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
    }
}
