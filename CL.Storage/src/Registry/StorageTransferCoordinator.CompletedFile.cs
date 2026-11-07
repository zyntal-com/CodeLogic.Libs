using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Registry;

internal static partial class StorageTransferCoordinator
{
    private static Result<StorageTransferSummary> CompletedFileSummary(StorageItem sourceFile, StagedContent staged,
        string destinationPath, Result<StorageItem> confirmed, StorageConditionEnforcement enforcement, string? stagingLeft, string? providerLeft)
    {
return Result<StorageTransferSummary>.Success(new StorageTransferSummary(StorageItemType.File, Files: 1,
                Directories: 0, Bytes: staged.Bytes, TransferredSources: [sourceFile.Path])
    {
        WrittenPath = destinationPath,
        Sha256 = staged.Sha256,
        VerifiedBy = staged.VerifiedBy,
        Destination = confirmed.IsSuccess ? confirmed.Value : null,
        BytesResumed = staged.BytesResumed,
        ConditionEnforcement = enforcement,
        TransferredItems = [sourceFile],
        StagingLeftBehind = stagingLeft,
        BackupLeftBehind = providerLeft
    });
    }
}
