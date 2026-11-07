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

    // ---------------------------------------------------------------- baseline

    /// <summary>
    /// Records what the run knows to be in sync: the pairs both sides agreed on when the plan was made (and the
    /// entries it carried over for paths it left alone), and the pairs the run's own steps produced. A path whose
    /// step did not complete — failed, stale, not run, withheld, a conflict — keeps its previous entry, so the next
    /// run sees it again. Nothing is taken from a listing made after the run, so an edit made meanwhile is not
    /// mistaken for the synced version. Where case is ignored, entries are matched ignoring case.
    /// </summary>
    private static async Task<(bool Saved, Error? Error)> SaveBaselineAsync(StorageSyncPlan plan, SyncRun run, StorageSyncOptions options, CancellationToken cancellationToken)
    {
        var store = options.StateStore!;
        try
        {
            var previous = await store.LoadAsync(options.SyncId!, cancellationToken).ConfigureAwait(false) ?? StorageSyncBaseline.Empty;
            var comparer = plan.CaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var before = new Dictionary<string, StorageSyncBaselineEntry>(comparer);
            foreach (var (path, entry) in previous.Entries) before.TryAdd(path, entry);
            var entries = new Dictionary<string, StorageSyncBaselineEntry>(comparer);
            foreach (var (path, entry) in plan.Agreed) entries[path] = entry;
            foreach (var result in run.Results.Where(result => result.Outcome != StorageSyncActionOutcome.Applied))
            {
                foreach (var path in new[] { result.Action.RelativePath, result.Action.TargetPath }.Where(path => path is { Length: > 0 }).Cast<string>())
                {
                    if (before.TryGetValue(path, out var kept)) entries[path] = kept;
                    else entries.Remove(path);
                }
            }
            foreach (var path in run.Removed.Keys) entries.Remove(path);
            foreach (var (path, entry) in run.Synced)
            {
                entries.Remove(path);
                entries[path] = entry;
            }
            var saved = await store.SaveAsync(options.SyncId!, new StorageSyncBaseline(previous.Generation + 1, new Dictionary<string, StorageSyncBaselineEntry>(entries, StringComparer.Ordinal)),
                plan.BaselineGeneration, cancellationToken).ConfigureAwait(false);
            return saved ? (true, null) : (false, StorageErrors.Conflict("Another run saved the baseline in between; the next run plans against it."));
        }
        catch (Exception error)
        {
            return (false, StorageErrors.FromException(error, "Save the sync baseline"));
        }
    }

    private static string Join(string root, string relative) =>
        root.Length == 0 ? relative : relative.Length == 0 ? root : $"{root}/{relative}";

    private sealed class PathProgress(IProgress<StorageTransferProgress> inner, string path) : IProgress<StorageTransferProgress>
    {
        public void Report(StorageTransferProgress value) => inner.Report(value with { ItemPath = path });
    }
}
