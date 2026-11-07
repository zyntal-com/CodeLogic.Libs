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

    private static List<string> TypeClashes(StorageDiff diff) =>
        [.. diff.Entries.Where(entry => entry.Reasons.HasFlag(StorageDiffReason.Type)).Select(entry => entry.RelativePath)];

    private static bool Under(List<string> roots, string path, bool insensitive)
    {
        var comparison = insensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return roots.Any(root => path.StartsWith(root + "/", comparison));
    }

    private enum Change { Absent, Unchanged, Created, Modified, Deleted }

    private static Change Classify(StorageSyncIdentity? before, StorageItem? now, TimeSpan tolerance) =>
        (before, now) switch
        {
            (null, null) => Change.Absent,
            (null, _) => Change.Created,
            (_, null) => Change.Deleted,
            _ => before.Matches(now, tolerance) ? Change.Unchanged : Change.Modified
        };

    private static (List<StorageSyncAction> Actions, int Unchanged, Dictionary<string, StorageSyncBaselineEntry> Agreed) PlanTwoWay(
        StorageDiff diff, StorageSyncBaseline baseline, StorageSyncOptions options, Scope scope, List<string> warnings)
    {
        var tolerance = options.Compare.TimeTolerance;
        var actions = new List<StorageSyncAction>();
        var agreed = new Dictionary<string, StorageSyncBaselineEntry>(StringComparer.Ordinal);
        var unchanged = 0;
        var hasBaseline = options.StateStore is not null;
        var insensitive = diff.CaseInsensitive;
        var baselineByKey = new Dictionary<string, (string Path, StorageSyncBaselineEntry Entry)>(StringComparer.Ordinal);
        foreach (var (path, value) in baseline.Entries.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!baselineByKey.TryAdd(StorageCompare.KeyOf(path, insensitive), (path, value)))
                warnings.Add($"The baseline holds '{baselineByKey[StorageCompare.KeyOf(path, insensitive)].Path}' and '{path}', the same name where case is ignored; the first was used.");
        }
        var entriesByKey = diff.Entries.ToDictionary(entry => StorageCompare.KeyOf(entry.RelativePath, insensitive), StringComparer.Ordinal);
        var clashes = TypeClashes(diff);

        foreach (var key in entriesByKey.Keys.Union(baselineByKey.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            entriesByKey.TryGetValue(key, out var entry);
            var base_ = baselineByKey.TryGetValue(key, out var b) ? b.Entry : null;
            // Steps name a path as the source spells it: the diff's spelling whenever it has the path (the source's
            // own, or for an item only at the destination the source's spelling of its folders), otherwise the one the
            // baseline recorded. Baseline entries are matched ignoring case where case is ignored, so the entry is
            // found again whatever happens to the step.
            var path = entry?.RelativePath ?? b.Path;
            var there = entry?.DestinationRelativePath ?? entry?.RelativePath ?? path;
            var isDirectory = entry?.IsDirectory ?? base_?.IsDirectory ?? false;
            void CarryOver()
            {
                if (base_ is not null) agreed[b.Path] = base_;
            }

            if (Under(clashes, path, insensitive)) { CarryOver(); continue; }
            if (entry is { Reasons: var r } && r.HasFlag(StorageDiffReason.Type))
            {
                warnings.Add($"'{path}' is a file on one side and a directory on the other; it and everything below it were left alone.");
                CarryOver();
                continue;
            }
            // Left out on either side, or by the current filters: nothing is known to have changed, so the entry
            // is kept for when the path takes part again.
            if (scope.LeftOut(path, there, isDirectory))
            {
                if (entry is null && scope.GoneFromBothSides(path, there, isDirectory)) continue;
                CarryOver();
                continue;
            }
            if (isDirectory)
            {
                PlanDirectory(actions, agreed, entry, path, hasBaseline && base_ is { IsDirectory: true }, options);
                continue;
            }

            var left = Classify(base_?.Source, entry?.Source, tolerance);
            var right = Classify(base_?.Destination, entry?.Destination, tolerance);
            if (!hasBaseline)
            {
                // Without a baseline nothing can be known to be deleted: missing files are copied, differences conflict.
                left = entry?.Source is null ? Change.Absent : Change.Created;
                right = entry?.Destination is null ? Change.Absent : Change.Created;
            }

            switch (left, right)
            {
                case (Change.Unchanged or Change.Absent, Change.Unchanged or Change.Absent) when entry?.Kind != StorageDiffKind.OnlyInSource && entry?.Kind != StorageDiffKind.OnlyInDestination:
                    if (entry is null) break;
                    // Neither side changed since the baseline, yet their content differs: the baseline cannot be
                    // trusted for this path, so it is a conflict rather than silently "in sync".
                    if (entry.Kind == StorageDiffKind.Different && (entry.Reasons & (StorageDiffReason.Size | StorageDiffReason.Checksum)) != 0)
                    {
                        Resolve(actions, entry, path, StorageSyncConflictKind.BothModified, options);
                        break;
                    }
                    unchanged++;
                    Agree(agreed, entry, path);
                    break;
                case (Change.Created or Change.Modified, Change.Unchanged or Change.Absent):
                    actions.Add(Copy(entry!, StorageSyncActionKind.CopyToDestination, path: path));
                    break;
                case (Change.Unchanged or Change.Absent, Change.Created or Change.Modified):
                    actions.Add(Copy(entry!, StorageSyncActionKind.CopyToSource, path: path));
                    break;
                case (Change.Deleted, Change.Unchanged):
                    actions.Add(options.PropagateDeletes
                        ? new StorageSyncAction { RelativePath = path, DestinationRelativePath = DestinationSpelling(entry, path), Kind = StorageSyncActionKind.DeleteFromDestination, Destination = StorageSyncIdentity.Of(entry!.Destination) }
                        : Copy(entry!, StorageSyncActionKind.CopyToSource, path: path));
                    break;
                case (Change.Unchanged, Change.Deleted):
                    actions.Add(options.PropagateDeletes
                        ? new StorageSyncAction { RelativePath = path, Kind = StorageSyncActionKind.DeleteFromSource, Source = StorageSyncIdentity.Of(entry!.Source) }
                        : Copy(entry!, StorageSyncActionKind.CopyToDestination, path: path));
                    break;
                case (Change.Deleted, Change.Deleted):
                case (Change.Deleted, Change.Absent):
                case (Change.Absent, Change.Deleted):
                    break;
                case (Change.Created, Change.Created) or (Change.Modified, Change.Modified) or (Change.Created, Change.Modified) or (Change.Modified, Change.Created):
                    if (entry!.Kind == StorageDiffKind.Same)
                    {
                        unchanged++;
                        Agree(agreed, entry, path);
                        break;
                    }
                    Resolve(actions, entry, path, left == Change.Modified && right == Change.Modified ? StorageSyncConflictKind.BothModified : StorageSyncConflictKind.BothCreated, options);
                    break;
                case (Change.Deleted, Change.Created or Change.Modified):
                case (Change.Created or Change.Modified, Change.Deleted):
                    ResolveDeleteVersusModify(actions, entry!, path, deletedOnSource: left == Change.Deleted, options);
                    break;
                default:
                    if (entry is not null && entry.Kind == StorageDiffKind.Same)
                    {
                        unchanged++;
                        Agree(agreed, entry, path);
                    }
                    break;
            }
        }
        KeepLinksWhole(actions, diff, baselineByKey, agreed, warnings);
        DropUnsafeDirectoryDeletes(actions, diff, baselineByKey, agreed);
        return (actions, unchanged, agreed);
    }

    private static void Agree(Dictionary<string, StorageSyncBaselineEntry> agreed, StorageDiffEntry entry, string path) =>
        agreed[path] = new StorageSyncBaselineEntry(StorageSyncIdentity.Of(entry.Source), StorageSyncIdentity.Of(entry.Destination));

    /// <summary>
    /// Plans a directory in a two-way sync: created on the side that lacks it, or — when the baseline had it on
    /// both sides — deleted where the other side deleted it (only once everything in it goes too).
    /// </summary>
    private static void PlanDirectory(
        List<StorageSyncAction> actions,
        Dictionary<string, StorageSyncBaselineEntry> agreed,
        StorageDiffEntry? entry,
        string path,
        bool wasOnBothSides,
        StorageSyncOptions options)
    {
        var atSource = entry?.Source is not null;
        var atDestination = entry?.Destination is not null;
        var there = DestinationSpelling(entry, path);
        if (atSource && atDestination)
        {
            agreed[path] = new StorageSyncBaselineEntry(null, null, IsDirectory: true);
            return;
        }
        if (atSource)
        {
            actions.Add(wasOnBothSides && options.PropagateDeletes
                ? new StorageSyncAction { RelativePath = path, Kind = StorageSyncActionKind.DeleteFromSource }
                : new StorageSyncAction { RelativePath = path, DestinationRelativePath = there, Kind = StorageSyncActionKind.CreateDirectory });
        }
        else if (atDestination)
        {
            actions.Add(wasOnBothSides && options.PropagateDeletes
                ? new StorageSyncAction { RelativePath = path, DestinationRelativePath = there, Kind = StorageSyncActionKind.DeleteFromDestination }
                : new StorageSyncAction { RelativePath = path, Kind = StorageSyncActionKind.CreateDirectoryAtSource });
        }
    }

    /// <summary>
    /// Takes out every step that would change a side through a link it followed (<see cref="StorageLinkHandling.Follow"/>):
    /// a delete of the link or of anything below a followed link to a folder, and a copy or rename onto a followed link
    /// to a file, which would replace the link (or write into its target). The path keeps its baseline entry, so the
    /// next run sees it the same way. Copies into a followed folder are kept: they land in the link's target.
    /// </summary>
    private static void KeepLinksWhole(
        List<StorageSyncAction> actions,
        StorageDiff diff,
        Dictionary<string, (string Path, StorageSyncBaselineEntry Entry)>? baselineByKey,
        Dictionary<string, StorageSyncBaselineEntry>? agreed,
        List<string> warnings)
    {
        var dropped = new HashSet<string>(diff.CaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        bool Through(StorageSyncAction action)
        {
            var there = action.DestinationRelativePath ?? action.RelativePath;
            return action.Kind switch
            {
                StorageSyncActionKind.DeleteFromDestination => diff.DestinationLinks.Through(there),
                StorageSyncActionKind.DeleteFromSource => diff.SourceLinks.Through(action.RelativePath),
                StorageSyncActionKind.CopyToDestination or StorageSyncActionKind.RenameAtDestination => diff.DestinationLinks.IsFileLink(there),
                StorageSyncActionKind.CopyToSource when action.TargetPath is null => diff.SourceLinks.IsFileLink(action.RelativePath),
                _ => false
            };
        }
        foreach (var action in actions.Where(Through).ToList())
        {
            actions.Remove(action);
            dropped.Add(action.RelativePath);
            var side = action.Kind is StorageSyncActionKind.DeleteFromSource or StorageSyncActionKind.CopyToSource ? "source" : "destination";
            warnings.Add($"'{action.RelativePath}' is a followed link at the {side}, or lies in one; nothing is deleted or replaced through a link, so it was left alone.");
            if (baselineByKey is not null && agreed is not null &&
                baselineByKey.TryGetValue(StorageCompare.KeyOf(action.RelativePath, diff.CaseInsensitive), out var previous))
                agreed[previous.Path] = previous.Entry;
        }
        // A kept conflict copy is only brought back to the source when its rename was taken.
        if (dropped.Count > 0)
            actions.RemoveAll(action => action.Kind == StorageSyncActionKind.CopyToSource && action.Conflict is not null &&
                                        action.TargetPath is { } target && dropped.Contains(target));
    }

    /// <summary>A copy of an item reached through a followed link reads its content where the link leads.</summary>
    private static List<StorageSyncAction> ReadThroughLinks(List<StorageSyncAction> actions, StorageDiff diff) =>
        [.. actions.Select(action => action.Kind switch
        {
            StorageSyncActionKind.CopyToDestination when diff.SourceLinks.ReadPath(action.RelativePath) is { } read => action with { ReadPath = read },
            StorageSyncActionKind.CopyToSource when action.TargetPath is null &&
                diff.DestinationLinks.ReadPath(action.DestinationRelativePath ?? action.RelativePath) is { } read => action with { ReadPath = read },
            _ => action
        })];
}
