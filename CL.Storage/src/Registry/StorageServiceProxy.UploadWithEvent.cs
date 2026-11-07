using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Events;
using CL.Storage.Models;
using CodeLogic.Core.Events;
using CodeLogic.Core.Results;
using System.Runtime.CompilerServices;

namespace CL.Storage.Registry;

internal sealed partial class StorageServiceProxy
{

    private async Task<Result<StorageItem>> UploadWithEventAsync(
        string requestedPath,
        Func<IStorageBackend, Task<Result<StorageItem>>> operation,
        string caller)
    {
        var lease = _library.AcquireOperation(_connectionId);
        Result<StorageItem> result;
        StorageEventPublisher? publisher = null;
        StorageItemWrittenEvent? @event = null;
        try
        {
            result = await operation(lease.Backend).ConfigureAwait(false);
            ReportFailure(lease.Backend, caller, requestedPath, result.Error);
            if (result.IsSuccess)
            {
                var eventPath = requestedPath;
                if (result.Value?.Path is { } returnedPath)
                {
                    var normalized = StoragePath.Normalize(returnedPath);
                    if (normalized.IsSuccess)
                        eventPath = normalized.Value!;
                }
                @event = new StorageItemWrittenEvent(
                    lease.Backend.ConnectionId,
                    lease.Backend.Provider,
                    eventPath,
                    DateTimeOffset.UtcNow);
                publisher = _library.CaptureEventPublisher();
            }
        }
        finally
        {
            lease.Dispose();
        }
        if (result.IsSuccess)
            await publisher!.PublishAsync(@event!).ConfigureAwait(false);
        return result;
    }

    private Task<Result> MutatePathWithEventAsync<TEvent>(
        string path,
        Result validation,
        CancellationToken cancellationToken,
        Func<IStorageBackend, string, Task<Result>> operation,
        Func<string, StorageProvider, string, TEvent> createEvent,
        [CallerMemberName] string caller = "")
        where TEvent : IEvent
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (validation.IsFailure)
            return Task.FromResult(Result.Failure(validation.Error!));
        var normalized = StoragePath.Normalize(path);
        return normalized.IsFailure
            ? Task.FromResult(Result.Failure(normalized.Error!))
            : MutateWithEventAsync(
                backend => operation(backend, normalized.Value!),
                (connectionId, provider) => createEvent(connectionId, provider, normalized.Value!),
                normalized.Value!,
                caller);
    }

    private async Task<Result> MutateWithEventAsync<TEvent>(
        Func<IStorageBackend, Task<Result>> operation,
        Func<string, StorageProvider, TEvent> createEvent,
        string path,
        string caller)
        where TEvent : IEvent
    {
        var lease = _library.AcquireOperation(_connectionId);
        Result result;
        StorageEventPublisher? publisher = null;
        TEvent? @event = default;
        try
        {
            result = await operation(lease.Backend).ConfigureAwait(false);
            ReportFailure(lease.Backend, caller, path, result.Error);
            if (result.IsSuccess)
            {
                @event = createEvent(lease.Backend.ConnectionId, lease.Backend.Provider);
                publisher = _library.CaptureEventPublisher();
            }
        }
        finally
        {
            lease.Dispose();
        }
        if (result.IsSuccess)
            await publisher!.PublishAsync(@event!).ConfigureAwait(false);
        return result;
    }
}
