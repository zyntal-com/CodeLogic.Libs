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
            if (NeedsMetadata(source, a) && await source.GetInfoAsync(a.Path, token).ConfigureAwait(false) is StorageItem full) a = full;
            if (NeedsMetadata(destination, b) && await destination.GetInfoAsync(b.Path, token).ConfigureAwait(false) is StorageItem other) b = other;
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
}
