using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Sync;

public static partial class StorageCompare
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
}
