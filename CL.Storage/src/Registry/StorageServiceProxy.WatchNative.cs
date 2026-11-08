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

    /// <summary>
    /// Watches natively when the connection's backend can. The lease is held only while the watch starts,
    /// so a long-running watch never blocks replacing the connection; it keeps watching the root it began on.
    /// </summary>
    public IAsyncEnumerable<Sync.StorageChange> WatchNativeAsync(string path, bool recursive, CancellationToken cancellationToken)
    {
        var normalized = StoragePath.Normalize(path);
        if (normalized.IsFailure)
            throw new ArgumentException(normalized.Error!.Message, nameof(path));
        using var lease = _library.AcquireOperation(_connectionId);
        return lease.Backend is Sync.IStorageWatchService native
            ? native.WatchNativeAsync(normalized.Value!, recursive, cancellationToken)
            : throw new NotSupportedException("This storage connection has no native change notifications.");
    }

    public async ValueTask<StorageConditionEnforcement> GetEnforcementAsync(
        StorageConditionKind kind,
        bool serverSideCopy,
        CancellationToken cancellationToken)
    {
        using var lease = _library.AcquireOperation(_connectionId);
        return await StorageConditionEnforcements.ForAsync(lease.Backend, kind, serverSideCopy, cancellationToken).ConfigureAwait(false);
    }

    private T Read<T>(Func<IStorageBackend, T> read)
    {
        using var lease = _library.AcquireOperation(_connectionId);
        return read(lease.Backend);
    }

    private async Task<T> InvokeAsync<T>(
        Func<IStorageBackend, Task<T>> operation,
        string? path = null,
        [CallerMemberName] string caller = "")
    {
        using var lease = _library.AcquireOperation(_connectionId);
        var result = await operation(lease.Backend).ConfigureAwait(false);
        ReportFailure(lease.Backend, caller, path, OperationFailure<T>.Of(result));
        return result;
    }

    /// <summary>Publishes <see cref="StorageOperationFailedEvent"/> for a failed backend call.</summary>
    private void ReportFailure(IStorageBackend backend, string caller, string? path, Error? error)
    {
        if (error is not null)
            _library.ConnectionObserver.OperationFailed(backend.ConnectionId, backend.Provider, OperationName(caller), path, error);
    }

    private static string OperationName(string caller) =>
        caller.EndsWith("Async", StringComparison.Ordinal) ? caller[..^"Async".Length] : caller;

    /// <summary>Reads the error from a <see cref="Result"/> or <see cref="Result{T}"/> without knowing which.</summary>
    private static class OperationFailure<T>
    {
        public static readonly Func<T, Error?> Of = Build();

        private static Func<T, Error?> Build()
        {
            if (typeof(T) == typeof(Result))
                return value => value is Result result ? result.Error : null;
            if (typeof(T).IsGenericType && typeof(T).GetGenericTypeDefinition() == typeof(Result<>))
            {
                var isFailure = typeof(T).GetProperty(nameof(Result.IsFailure))!;
                var error = typeof(T).GetProperty(nameof(Result.Error))!;
                return value => value is not null && (bool)isFailure.GetValue(value)! ? (Error?)error.GetValue(value) : null;
            }
            return _ => null;
        }
    }

    public Task<Result> SetPermissionsAsync(string path, int unixMode, CancellationToken cancellationToken = default) =>
        unixMode is < 0 or > UnixPermissions.MaxMode
            ? Task.FromResult(Result.Failure(StorageErrors.InvalidContent("The permission mode must be between 0 and octal 7777.")))
            : InvokePathAsync(path, cancellationToken, (backend, normalized) => backend is IStorageAttributeService attributes
                ? attributes.SetPermissionsAsync(normalized, unixMode, cancellationToken)
                : Task.FromResult(Result.Failure(StorageErrors.Unsupported("This storage connection does not support permissions."))));

    public Task<Result> SetOwnerAsync(string path, long? ownerId, long? groupId, CancellationToken cancellationToken = default) =>
        ownerId is null && groupId is null
            ? Task.FromResult(Result.Failure(StorageErrors.InvalidContent("An owner or group ID is required.")))
            : InvokePathAsync(path, cancellationToken, (backend, normalized) => backend is IStorageAttributeService attributes
                ? attributes.SetOwnerAsync(normalized, ownerId, groupId, cancellationToken)
                : Task.FromResult(Result.Failure(StorageErrors.Unsupported("This storage connection does not support ownership changes."))));

    public Task<Result> SetTimestampsAsync(string path, DateTimeOffset? lastModified, DateTimeOffset? lastAccessed = null, CancellationToken cancellationToken = default) =>
        lastModified is null && lastAccessed is null
            ? Task.FromResult(Result.Failure(StorageErrors.InvalidContent("A modification or access time is required.")))
            : InvokePathAsync(path, cancellationToken, (backend, normalized) => backend is IStorageAttributeService attributes
                ? attributes.SetTimestampsAsync(normalized, lastModified, lastAccessed, cancellationToken)
                : Task.FromResult(Result.Failure(StorageErrors.Unsupported("This storage connection does not support setting timestamps."))));

    public Task<Result> CreateLinkAsync(string linkPath, string targetPath, CancellationToken cancellationToken = default)
    {
        var target = StoragePath.Normalize(targetPath);
        if (target.IsFailure)
            return Task.FromResult(Result.Failure(target.Error!));
        return InvokePathAsync(linkPath, cancellationToken, (backend, normalized) => backend is IStorageAttributeService attributes
            ? attributes.CreateLinkAsync(normalized, target.Value!, cancellationToken)
            : Task.FromResult(Result.Failure(StorageErrors.Unsupported("This storage connection does not support creating links."))));
    }

    public Task<Result<StorageChecksum>> GetServerChecksumAsync(string path, StorageChecksumAlgorithm algorithm, CancellationToken cancellationToken = default) =>
        InvokePathAsync(path, cancellationToken, (backend, normalized) => backend is IStorageChecksumService checksums
            ? checksums.GetServerChecksumAsync(normalized, algorithm, cancellationToken)
            : Task.FromResult(Result<StorageChecksum>.Failure(StorageErrors.Unsupported("This storage connection does not report server checksums."))));

    public Task<Result<StorageItem>> AppendAsync(string path, Stream source, CancellationToken cancellationToken = default) =>
        UploadPathWithEventAsync(path, Result.Success(), cancellationToken, (backend, normalized) => backend is IStorageAppendService append
            ? append.AppendAsync(normalized, source, cancellationToken)
            : Task.FromResult(Result<StorageItem>.Failure(StorageErrors.Unsupported("This storage connection cannot append."))));

    public Task<Result<StorageCommandResult>> ExecuteCommandAsync(string command, CancellationToken cancellationToken = default) =>
        InvokeAsync(backend => backend is IStorageCommandService commands
            ? commands.ExecuteCommandAsync(command, cancellationToken)
            : Task.FromResult(Result<StorageCommandResult>.Failure(StorageErrors.Unsupported("This storage connection does not support raw commands."))));

    public Task<Result<StorageSpaceInfo>> GetSpaceAsync(string path = "", CancellationToken cancellationToken = default) =>
        InvokePathAsync(path, cancellationToken, (backend, normalized) => backend is IStorageSpaceService space
            ? space.GetSpaceAsync(normalized, cancellationToken)
            : Task.FromResult(Result<StorageSpaceInfo>.Failure(StorageErrors.Unsupported("This storage connection does not report free space."))));

    public Task<Result<StorageLinkInfo>> ReadLinkAsync(string path, CancellationToken cancellationToken = default) =>
        InvokePathAsync(path, cancellationToken, (backend, normalized) => backend is IStorageAttributeService attributes
            ? attributes.ReadLinkAsync(normalized, cancellationToken)
            : Task.FromResult(Result<StorageLinkInfo>.Failure(StorageErrors.Unsupported("This storage connection does not support reading links."))));

    private Task<Result<T>> InvokePathAsync<T>(
        string path,
        CancellationToken cancellationToken,
        Func<IStorageBackend, string, Task<Result<T>>> operation,
        [CallerMemberName] string caller = "") =>
        InvokePathAsync(path, cancellationToken, Result.Success(), operation, caller);

    private Task<Result<T>> InvokePathAsync<T>(
        string path,
        CancellationToken cancellationToken,
        Result validation,
        Func<IStorageBackend, string, Task<Result<T>>> operation,
        [CallerMemberName] string caller = "")
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (validation.IsFailure)
            return Task.FromResult(Result<T>.Failure(validation.Error!));
        var normalized = StoragePath.Normalize(path);
        return normalized.IsFailure
            ? Task.FromResult(Result<T>.Failure(normalized.Error!))
            : InvokeAsync(backend => operation(backend, normalized.Value!), normalized.Value!, caller);
    }

    private Task<Result> InvokePathAsync(
        string path,
        CancellationToken cancellationToken,
        Func<IStorageBackend, string, Task<Result>> operation,
        [CallerMemberName] string caller = "")
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = StoragePath.Normalize(path);
        return normalized.IsFailure
            ? Task.FromResult(Result.Failure(normalized.Error!))
            : InvokeAsync(backend => operation(backend, normalized.Value!), normalized.Value!, caller);
    }

    private async Task<Result<Stream>> DownloadWithLeaseAsync(
        string path,
        StorageDownloadOptions? options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var validation = options?.Validate() ?? Result.Success();
        if (validation.IsFailure)
            return Result<Stream>.Failure(validation.Error!);
        var normalized = StoragePath.Normalize(path);
        if (normalized.IsFailure)
            return Result<Stream>.Failure(normalized.Error!);

        var lease = _library.AcquireOperation(_connectionId);
        try
        {
            var result = await lease.Backend.DownloadAsync(normalized.Value!, options, cancellationToken).ConfigureAwait(false);
            if (result.IsFailure)
            {
                ReportFailure(lease.Backend, nameof(DownloadAsync), normalized.Value!, result.Error);
                lease.Dispose();
                return Result<Stream>.Failure(result.Error!);
            }
            return Result<Stream>.Success(new LeaseOwnedStream(result.Value!, lease));
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private Task<Result<StorageItem>> UploadPathWithEventAsync(
        string path,
        Result validation,
        CancellationToken cancellationToken,
        Func<IStorageBackend, string, Task<Result<StorageItem>>> operation,
        [CallerMemberName] string caller = "")
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (validation.IsFailure)
            return Task.FromResult(Result<StorageItem>.Failure(validation.Error!));
        var normalized = StoragePath.Normalize(path);
        return normalized.IsFailure
            ? Task.FromResult(Result<StorageItem>.Failure(normalized.Error!))
            : UploadWithEventAsync(normalized.Value!, backend => operation(backend, normalized.Value!), caller);
    }
}
