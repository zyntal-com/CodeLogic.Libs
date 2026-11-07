using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Sync;

/// <summary>What identifies one version of a file: size, time, ETag, version, and a digest when known.</summary>
/// <param name="Size">Length in bytes.</param>
/// <param name="Modified">Modification time (the source time kept in metadata on stores that cannot set times).</param>
/// <param name="ETag">Provider entity tag.</param>
/// <param name="VersionId">Provider version.</param>
/// <param name="Sha256">Content digest, when computed.</param>
public sealed record StorageSyncIdentity(long? Size, DateTimeOffset? Modified, string? ETag, string? VersionId, string? Sha256 = null)
{
    internal static StorageSyncIdentity? Of(StorageItem? item) =>
        item is null || item.ItemType == StorageItemType.Directory
            ? null
            : new StorageSyncIdentity(item.Size, StorageCompare.EffectiveModified(item), item.ETag, item.VersionId, item.Sha256)
            {
                ModifiedPrecision = item.ModifiedPrecision
            };

    /// <summary>
    /// How coarse <see cref="StorageSyncIdentity.Modified"/> was when it was read (an FTP listing without MLSD gives
    /// minutes); null when exact. See <see cref="StorageItem.ModifiedPrecision"/>.
    /// </summary>
    public TimeSpan? ModifiedPrecision { get; init; }

    /// <summary>Whether an item is still this version: by version, else ETag, else size and time.</summary>
    internal bool Matches(StorageItem? item, TimeSpan tolerance)
    {
        if (item is null || item.ItemType == StorageItemType.Directory) return false;
        if (VersionId is not null && item.VersionId is not null) return VersionId == item.VersionId;
        if (ETag is not null && item.ETag is not null)
        {
            // A mismatch proves a change; only a strong match proves there was none (weak validators fall through).
            if (!Providers.StorageETags.WeakEquals(ETag, item.ETag)) return false;
            if (!Providers.StorageETags.IsWeak(ETag) && !Providers.StorageETags.IsWeak(item.ETag)) return true;
        }
        var modified = StorageCompare.EffectiveModified(item);
        return Size == item.Size &&
               (Modified is null || modified is null ||
                SameTime(Modified.Value, ModifiedPrecision, modified.Value, item.ModifiedPrecision, tolerance));
    }

    /// <summary>Whether two identities describe the same version.</summary>
    internal bool SameVersionAs(StorageSyncIdentity? other, TimeSpan tolerance)
    {
        if (other is null) return false;
        if (VersionId is not null && other.VersionId is not null) return VersionId == other.VersionId;
        if (ETag is not null && other.ETag is not null)
        {
            if (!Providers.StorageETags.WeakEquals(ETag, other.ETag)) return false;
            if (!Providers.StorageETags.IsWeak(ETag) && !Providers.StorageETags.IsWeak(other.ETag)) return true;
        }
        return Size == other.Size && (Modified is null || other.Modified is null ||
            SameTime(Modified.Value, ModifiedPrecision, other.Modified.Value, other.ModifiedPrecision, tolerance));
    }

    /// <summary>
    /// Whether two modification times agree within <paramref name="tolerance"/>, where a coarse time (one with a
    /// precision) stands for the interval [time, time + precision) it was truncated from.
    /// </summary>
    internal static bool SameTime(DateTimeOffset a, TimeSpan? aPrecision, DateTimeOffset b, TimeSpan? bPrecision, TimeSpan tolerance)
    {
        var aHigh = a + (aPrecision ?? TimeSpan.Zero);
        var bHigh = b + (bPrecision ?? TimeSpan.Zero);
        if (aPrecision is null && bPrecision is null) return (a - b).Duration() <= tolerance;
        // The intervals, widened by the tolerance, overlap; a coarse interval is half-open.
        return a - bHigh < tolerance + (bPrecision is null ? TimeSpan.FromTicks(1) : TimeSpan.Zero) &&
               b - aHigh < tolerance + (aPrecision is null ? TimeSpan.FromTicks(1) : TimeSpan.Zero);
    }
}
