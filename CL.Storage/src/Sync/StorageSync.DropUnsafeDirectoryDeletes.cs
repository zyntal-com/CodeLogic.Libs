using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CodeLogic.Core.Results;

namespace CL.Storage.Sync;

public static partial class StorageSync
{

    /// <summary>
    /// Keeps a directory deletion only when everything inside it on that side is deleted in the same plan and
    /// nothing inside it was left out; otherwise the directory stays (and keeps its baseline entry, so the side
    /// that deleted it does not get it back), and the kept items inside are synced as usual.
    /// </summary>
    private static void DropUnsafeDirectoryDeletes(
        List<StorageSyncAction> actions,
        StorageDiff diff,
        Dictionary<string, (string Path, StorageSyncBaselineEntry Entry)>? baselineByKey,
        Dictionary<string, StorageSyncBaselineEntry>? agreed)
    {
        var comparer = diff.CaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var directories = diff.Entries.Where(entry => entry.IsDirectory).Select(entry => entry.RelativePath).ToHashSet(comparer);
        foreach (var kind in new[] { StorageSyncActionKind.DeleteFromSource, StorageSyncActionKind.DeleteFromDestination })
        {
            var onSource = kind == StorageSyncActionKind.DeleteFromSource;
            var exclusions = onSource ? diff.SourceExclusions : diff.DestinationExclusions;
            var deleted = actions.Where(action => action.Kind == kind).Select(action => action.RelativePath).ToHashSet(comparer);
            var folderDeletes = actions.Where(action => action.Kind == kind && directories.Contains(action.RelativePath)).ToList();
            if (folderDeletes.Count == 0) continue;

            // Every folder that still holds something on this side after the plan: the parents of what stays.
            var kept = new HashSet<string>(comparer);
            void KeepParents(string path)
            {
                for (var parent = StorageExclusions.Parent(path); parent.Length > 0 && kept.Add(parent); parent = StorageExclusions.Parent(parent))
                {
                }
            }
            foreach (var entry in diff.Entries)
            {
                if ((onSource ? entry.Source : entry.Destination) is not null && !deleted.Contains(entry.RelativePath))
                    KeepParents(entry.RelativePath);
            }

            foreach (var directory in folderDeletes.OrderByDescending(action => action.RelativePath.Count(c => c == '/')))
            {
                var there = onSource ? directory.RelativePath : directory.DestinationRelativePath ?? directory.RelativePath;
                if (!kept.Contains(directory.RelativePath) && !exclusions.Keeps(there)) continue;
                actions.Remove(directory);
                KeepParents(directory.RelativePath);
                if (baselineByKey is not null && agreed is not null &&
                    baselineByKey.TryGetValue(StorageCompare.KeyOf(directory.RelativePath, diff.CaseInsensitive), out var previous))
                    agreed[previous.Path] = previous.Entry;
            }
        }
    }

    private static void Resolve(List<StorageSyncAction> actions, StorageDiffEntry entry, string path, StorageSyncConflictKind kind, StorageSyncOptions options)
    {
        switch (options.ConflictPolicy)
        {
            case StorageSyncConflictPolicy.NewerWins:
                var a = StorageCompare.EffectiveModified(entry.Source!);
                var b = StorageCompare.EffectiveModified(entry.Destination!);
                if (a is { } left && b is { } right && (left - right).Duration() > options.Compare.TimeTolerance)
                {
                    actions.Add(Copy(entry, left > right ? StorageSyncActionKind.CopyToDestination : StorageSyncActionKind.CopyToSource, kind, path));
                    return;
                }
                break;
            case StorageSyncConflictPolicy.KeepBoth:
                var theirs = StorageSyncIdentity.Of(entry.Destination);
                var conflictPath = ConflictPath(path, theirs!);
                var there = entry.DestinationRelativePath ?? entry.RelativePath;
                var conflictThere = Sibling(there, conflictPath);
                actions.Add(new StorageSyncAction { RelativePath = path, DestinationRelativePath = DestinationSpelling(entry, path), Kind = StorageSyncActionKind.RenameAtDestination, TargetPath = conflictPath, Destination = theirs, Conflict = kind });
                actions.Add(Copy(entry, StorageSyncActionKind.CopyToDestination, kind, path) with { Destination = null });
                actions.Add(new StorageSyncAction
                {
                    RelativePath = conflictPath,
                    DestinationRelativePath = string.Equals(conflictThere, conflictPath, StringComparison.Ordinal) ? null : conflictThere,
                    Kind = StorageSyncActionKind.CopyToSource,
                    Conflict = kind,
                    Bytes = entry.Destination?.Size,
                    Destination = theirs,
                    TargetPath = path
                });
                return;
        }
        actions.Add(new StorageSyncAction
        {
            RelativePath = path,
            DestinationRelativePath = DestinationSpelling(entry, path),
            Kind = StorageSyncActionKind.Conflict,
            Reason = entry.Reasons,
            Source = StorageSyncIdentity.Of(entry.Source),
            Destination = StorageSyncIdentity.Of(entry.Destination),
            Conflict = kind
        });
    }

    private static void ResolveDeleteVersusModify(List<StorageSyncAction> actions, StorageDiffEntry entry, string path, bool deletedOnSource, StorageSyncOptions options)
    {
        if (options.ConflictPolicy is StorageSyncConflictPolicy.KeepBoth or StorageSyncConflictPolicy.NewerWins)
        {
            // Keep the edit: copy the modified file back to the side that deleted it.
            actions.Add(Copy(entry, deletedOnSource ? StorageSyncActionKind.CopyToSource : StorageSyncActionKind.CopyToDestination, StorageSyncConflictKind.DeleteVersusModify, path));
            return;
        }
        actions.Add(new StorageSyncAction
        {
            RelativePath = path,
            DestinationRelativePath = DestinationSpelling(entry, path),
            Kind = StorageSyncActionKind.Conflict,
            Source = StorageSyncIdentity.Of(entry.Source),
            Destination = StorageSyncIdentity.Of(entry.Destination),
            Conflict = StorageSyncConflictKind.DeleteVersusModify
        });
    }

    /// <summary>
    /// <c>name (conflict xxxxxxxx).ext</c>, from the kept version's identity so repeated runs pick the same name;
    /// a compound extension such as <c>.tar.gz</c> stays whole.
    /// </summary>
    internal static string ConflictPath(string path, StorageSyncIdentity identity, int attempt = 1)
    {
        var seed = $"{identity.Size}|{identity.Modified?.UtcTicks}|{identity.ETag}|{identity.VersionId}";
        var tag = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seed)))[..8];
        return ConflictResolverName(path, attempt == 1 ? $"conflict {tag}" : $"conflict {tag} {attempt}");
    }

    private static string ConflictResolverName(string path, string label)
    {
        var slash = path.LastIndexOf('/');
        var directory = slash < 0 ? string.Empty : path[..(slash + 1)];
        var name = path[(slash + 1)..];
        var dot = name.LastIndexOf('.');
        if (dot > 4 && name.LastIndexOf(".tar", dot, StringComparison.OrdinalIgnoreCase) == dot - 4) dot -= 4;
        var (stem, extension) = dot > 0 ? (name[..dot], name[dot..]) : (name, string.Empty);
        return $"{directory}{stem} ({label}){extension}";
    }

    /// <summary>A file beside <paramref name="path"/>, named like <paramref name="named"/>.</summary>
    private static string Sibling(string path, string named)
    {
        var slash = path.LastIndexOf('/');
        var name = named[(named.LastIndexOf('/') + 1)..];
        return slash < 0 ? name : $"{path[..slash]}/{name}";
    }

    /// <summary>Withholds every deletion when a limit or an empty side makes the run look unsafe.</summary>
    private static List<StorageSyncAction> ApplyDeletionSafety(
        List<StorageSyncAction> actions,
        StorageSyncBaseline baseline,
        StorageTree source,
        StorageTree destination,
        StorageSyncOptions options,
        List<string> warnings)
    {
        var deletes = actions.Where(action => action.Kind is StorageSyncActionKind.DeleteFromDestination or StorageSyncActionKind.DeleteFromSource).ToList();
        if (deletes.Count == 0) return actions;

        static int Files(StorageTree tree) => tree.Items.Values.Count(item => item.ItemType != StorageItemType.Directory);
        // Every file is its own step; a directory step only removes an emptied folder, so it counts for nothing.
        var destinationDeletes = deletes.Count(action => action.Kind == StorageSyncActionKind.DeleteFromDestination && action.Destination is not null);
        var sourceDeletes = deletes.Count(action => action.Kind == StorageSyncActionKind.DeleteFromSource && action.Source is not null);
        var sourceFiles = Files(source);
        var destinationFiles = Files(destination);

        string? reason = null;
        var baselineFiles = baseline.Entries.Values.Count(entry => !entry.IsDirectory);
        if (!options.AllowEmptySide)
        {
            if (sourceFiles == 0 && (destinationFiles > 0 || baselineFiles > 0))
                reason = "the source is unexpectedly empty";
            else if (destinationFiles == 0 && options.Direction == StorageSyncDirection.TwoWay && baselineFiles > 0)
                reason = "the destination is unexpectedly empty";
        }
        var total = destinationDeletes + sourceDeletes;
        if (reason is null && options.MaxDeletes is { } maxDeletes && total > maxDeletes)
            reason = $"{total} file deletion(s) exceed MaxDeletes ({maxDeletes})";
        if (reason is null && options.MaxDeletePercent is { } maxPercent)
        {
            var destinationShare = destinationFiles == 0 ? 0 : destinationDeletes * 100.0 / destinationFiles;
            var sourceShare = sourceFiles == 0 ? 0 : sourceDeletes * 100.0 / sourceFiles;
            var share = Math.Max(destinationShare, sourceShare);
            if (share > maxPercent)
                reason = $"deleting {Percent(share, maxPercent)}% of a side's files exceeds MaxDeletePercent ({maxPercent.ToString(CultureInfo.InvariantCulture)}%)";
        }
        if (reason is null) return actions;
        var folders = deletes.Count - total;
        warnings.Add($"All {total} file deletion(s){(folders > 0 ? $" and {folders} folder deletion(s)" : string.Empty)} were withheld because {reason}.");
        return [.. actions.Select(action => action.Kind is StorageSyncActionKind.DeleteFromDestination or StorageSyncActionKind.DeleteFromSource
            ? action with { WithheldReason = reason }
            : action)];
    }

    /// <summary>A share with as many decimals as it takes to differ from the limit it exceeds ("50.01", never "50").</summary>
    private static string Percent(double share, double limit)
    {
        var text = share.ToString("0.#", CultureInfo.InvariantCulture);
        for (var digits = 2; digits <= 6 && double.Parse(text, CultureInfo.InvariantCulture) <= limit; digits++)
            text = share.ToString("0." + new string('#', digits), CultureInfo.InvariantCulture);
        return text;
    }

    /// <summary>Directories first (shallowest first), then renames, copies, and deletions (deepest first).</summary>
    private static IReadOnlyList<StorageSyncAction> Order(List<StorageSyncAction> actions)
    {
        static int Rank(StorageSyncActionKind kind) => kind switch
        {
            StorageSyncActionKind.CreateDirectory or StorageSyncActionKind.CreateDirectoryAtSource => 0,
            StorageSyncActionKind.RenameAtDestination => 1,
            StorageSyncActionKind.CopyToDestination or StorageSyncActionKind.CopyToSource => 2,
            StorageSyncActionKind.Conflict => 3,
            _ => 4
        };
        static int Depth(StorageSyncAction action) => action.RelativePath.Length == 0 ? -1 : action.RelativePath.Count(c => c == '/');
        return [.. actions
            .Select((action, index) => (action, index))
            .OrderBy(pair => Rank(pair.action.Kind))
            .ThenBy(pair => Rank(pair.action.Kind) switch { 0 => Depth(pair.action), 4 => -Depth(pair.action), _ => 0 })
            .ThenBy(pair => pair.index)
            .Select(pair => pair.action)];
    }
}
