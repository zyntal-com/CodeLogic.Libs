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
