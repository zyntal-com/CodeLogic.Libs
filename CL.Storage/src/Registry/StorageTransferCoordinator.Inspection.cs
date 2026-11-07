using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Registry;

internal static partial class StorageTransferCoordinator
{
    private static async Task<Result<StorageItem?>> InspectTransferDestinationAsync(IStorageBackend destination, string path, CancellationToken ct)
    {
        var exists = await destination.ExistsAsync(path, ct).ConfigureAwait(false);
        if (exists.IsFailure) return exists.Error!;
        if (!exists.Value) return Result<StorageItem?>.Success(null);
        var info = await destination.GetInfoAsync(path, ct).ConfigureAwait(false);
        if (info.IsFailure) return info.Error!;
        if (info.Value!.ItemType != StorageItemType.File)
            return StorageErrors.Conflict($"The transfer destination '{path}' is not a file.");
        return info.Value;
    }

    private static Result<IReadOnlyDictionary<string, string>> TransferMetadata(StorageItem source, IStorageBackend destination,
        StorageMetadataPreservation policy)
    {
        if (policy == StorageMetadataPreservation.Discard)
            return Result<IReadOnlyDictionary<string, string>>.Success(new Dictionary<string, string>());
        if (source.Metadata.Count > 0 && !destination.Capabilities.Supports(StorageFeature.MetadataWrite))
        {
            if (policy == StorageMetadataPreservation.Require)
                return StorageErrors.Unsupported("The destination cannot preserve source user metadata.");
            return Result<IReadOnlyDictionary<string, string>>.Success(new Dictionary<string, string>());
        }
        return Result<IReadOnlyDictionary<string, string>>.Success(source.Metadata);
    }
}
