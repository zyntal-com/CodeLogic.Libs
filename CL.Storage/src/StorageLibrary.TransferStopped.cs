using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Events;
using CL.Storage.Models;
using CL.Storage.Providers;
using CL.Storage.Providers.Azure;
using CL.Storage.Providers.Ftp;
using CL.Storage.Providers.GoogleCloud;
using CL.Storage.Providers.Local;
using CL.Storage.Providers.S3;
using CL.Storage.Providers.Sftp;
using CL.Storage.Providers.Swift;
using CL.Storage.Providers.WebDav;
using CL.Storage.Registry;
using CodeLogic.Core.Configuration;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using CodeLogic.Framework.Libraries;

namespace CL.Storage;

public sealed partial class StorageLibrary
{
    private async Task<(StorageTransferReport Report, StorageEventPublisher? Publisher, StorageTransferSummary? Summary, bool AnnounceAsCopy)> StoppedTransferReportAsync(
        StorageTransferReport report, TransferState? state, bool move, IStorageBackend? movedSource,
        StorageEventPublisher? publisher, StorageTransferSummary? summary, bool announceAsCopy, Error error)
    {
        if (!report.DestinationCommitted)
        {
            var stopped = state is null
                ? report with
                {
                    Outcome = error.Code == StorageErrors.CancelledCode ? StorageTransferOutcome.Cancelled : StorageTransferOutcome.Failed,
                    Error = error,
                    SourceDeleted = move ? false : null
                }
                : FailedTransferReport(report, error, state, move);
            if (stopped.DestinationCommitted)
            {
                announceAsCopy = true;
                publisher ??= CaptureEventPublisher();
                summary ??= new StorageTransferSummary(stopped.SourceType ?? StorageItemType.File,
                    state!.FilesCommitted, state.DirectoriesCreated, state.BytesCommitted);
            }
            return (stopped, publisher, summary, announceAsCopy);
        }
        var sourceDeleted = false;
        if (move && movedSource is not null)
            sourceDeleted = await IsGoneAsync(movedSource, report.SourcePath).ConfigureAwait(false);
        publisher ??= CaptureEventPublisher();
        if (!move || sourceDeleted)
            return (report with
            {
                Outcome = StorageTransferOutcome.Completed,
                Error = null,
                SourceDeleted = move ? true : null
            }, publisher, summary, announceAsCopy);
        announceAsCopy = true;
        var details = $"{StorageErrorInfo.DestinationStateKey}=complete";
        if (state is { SourceItemsDeleted: > 0 })
            details += $";sourceItemsDeleted={state.SourceItemsDeleted}";
        return (report with
        {
            Outcome = StorageTransferOutcome.NeedsReconciliation,
            Error = StagedWriter.AppendDetails(error, details),
            SourceDeleted = false
        }, publisher, summary, announceAsCopy);
    }
}
