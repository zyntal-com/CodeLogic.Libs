using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage;

public sealed partial class StorageLibrary
{
    private static Result<(string Source, string Destination)> PrepareTransferPaths(string source, string destination, StorageTransferOptions options)
    {
        var validation = options.Validate();
        if (validation.IsFailure) return validation.Error!;
        var normalizedSource = NormalizeTransferPath(source, "source");
        if (normalizedSource.IsFailure) return normalizedSource.Error!;
        var normalizedDestination = NormalizeTransferPath(destination, "destination");
        if (normalizedDestination.IsFailure) return normalizedDestination.Error!;
        return (normalizedSource.Value!, normalizedDestination.Value!);
    }
}
