using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CodeLogic.Core.Results;

namespace CL.Storage.Providers.Azure;

public sealed partial class AzureBlobStorageBackend
{

    /// <inheritdoc />
    public bool TryGetNativeClient<TClient>([NotNullWhen(true)] out TClient? client) where TClient : class
    {
        client = _container as TClient;
        return client is not null;
    }

    /// <inheritdoc />
    public Task<Result<NativeConnectionLease<TClient>>> OpenNativeConnectionAsync<TClient>(CancellationToken cancellationToken = default) where TClient : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_container is not TClient typed)
            return Task.FromResult(Result<NativeConnectionLease<TClient>>.Failure(StorageErrors.Unsupported($"Azure Blob does not expose native type '{typeof(TClient).FullName}'.")));
        return Task.FromResult(Result<NativeConnectionLease<TClient>>.Success(new NativeConnectionLease<TClient>(typed, _ => ValueTask.CompletedTask)));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<Result<StorageItem>> GetDirectoryInfoAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var _ in _container.GetBlobsAsync(BlobTraits.None, BlobStates.None, DirectoryPrefix(path), cancellationToken).ConfigureAwait(false))
                return Result<StorageItem>.Success(DirectoryItem(path));
            return Result<StorageItem>.Failure(StorageErrors.NotFound($"Azure blob item '{path}' was not found."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Get Azure blob directory info")); }
    }

    private BlobClient Blob(string path) => _container.GetBlobClient(ToKey(path));
    private string ToKey(string path) => _prefix + path;
    private string DirectoryPrefix(string path) => path.Length == 0 ? _prefix : ToKey(path).TrimEnd('/') + "/";
    /// <summary>The storage path of a blob name under the mounted prefix, or null for a name the library cannot address (see <see cref="ObjectKeyPaths"/>).</summary>
    private string? FromKey(string key) => ObjectKeyPaths.FromKey(_prefix, key);
    private Result<string> Normalize(string path) => StoragePath.Normalize(path);
    private Result<string> NormalizeRequired(string path)
    {
        var normalized = Normalize(path);
        return normalized.IsFailure || normalized.Value!.Length > 0
            ? normalized
            : Result<string>.Failure(StorageErrors.InvalidPath("A non-root storage path is required."));
    }

    private StorageItem? ToItem(BlobHierarchyItem hierarchy)
    {
        if (hierarchy.IsPrefix)
        {
            // An unaddressable name ("a/../b", "a//b") is skipped: the library could never name it back.
            var path = FromKey(hierarchy.Prefix)?.TrimEnd('/');
            return string.IsNullOrEmpty(path) ? null : DirectoryItem(path);
        }
        return hierarchy.Blob is null ? null : ToItem(hierarchy.Blob);
    }

    private StorageItem? ToItem(BlobItem blob)
    {
        if (FromKey(blob.Name) is not { Length: > 0 } path) return null;
        if (path.EndsWith('/')) return DirectoryItem(path.TrimEnd('/'));
        return new StorageItem
        {
            Path = path,
            Name = NameOf(path),
            ItemType = StorageItemType.File,
            Size = blob.Properties.ContentLength,
            LastModified = blob.Properties.LastModified,
            ContentType = blob.Properties.ContentType,
            ETag = blob.Properties.ETag?.ToString().Trim('"'),
            VersionId = blob.VersionId,
            Metadata = blob.Metadata is null
                ? []
                : new Dictionary<string, string>(blob.Metadata, StringComparer.Ordinal)
        };
    }

    private static StorageItem ToItem(string path, BlobProperties properties) => new()
    {
        Path = path,
        Name = NameOf(path),
        ItemType = path.EndsWith('/') ? StorageItemType.Directory : StorageItemType.File,
        Size = path.EndsWith('/') ? null : properties.ContentLength,
        LastModified = properties.LastModified,
        ContentType = properties.ContentType,
        ETag = properties.ETag.ToString().Trim('"'),
        VersionId = properties.VersionId,
        Metadata = new Dictionary<string, string>(properties.Metadata, StringComparer.Ordinal)
    };

    private static Result ValidateCurrentCondition(
        StorageItem current,
        StorageMutationCondition? condition,
        string itemName)
    {
        if (condition is null or { IsEmpty: true })
            return Result.Success();
        if (condition.ExpectedETag is not null &&
            !string.Equals(
                condition.ExpectedETag.Trim('"'),
                current.ETag?.Trim('"'),
                StringComparison.Ordinal))
        {
            return Result.Failure(StorageErrors.Conflict(
                $"The {itemName} ETag no longer matches the requested condition."));
        }
        return condition.ExpectedVersionId is not null &&
               !string.Equals(condition.ExpectedVersionId, current.VersionId, StringComparison.Ordinal)
            ? Result.Failure(StorageErrors.Conflict(
                $"The {itemName} version no longer matches the requested condition."))
            : Result.Success();
    }

    private static StorageItem DirectoryItem(string path) => new()
    {
        Path = path,
        Name = path.Length == 0 ? string.Empty : NameOf(path),
        ItemType = StorageItemType.Directory
    };

    private static string NameOf(string path) => path.Split('/')[^1];

    private static Error Map(Exception exception, string operation)
    {
        if (exception is RequestFailedException azure)
        {
            if (azure.Status == 403 && azure.ErrorCode is "AuthenticationFailed" or "InvalidAuthenticationInfo")
                return StorageErrors.AuthenticationFailed(
                    $"{operation}: Azure Blob rejected the credentials.",
                    ProviderErrorMapper.Details(StorageErrorInfo.HttpStatusKey, "403", azure.ErrorCode));
            if (azure.Status > 0)
            {
                TimeSpan? retryAfter = null;
                var response = azure.GetRawResponse();
                if (response is not null && response.Headers.TryGetValue("Retry-After", out var header) &&
                    int.TryParse(header, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
                    retryAfter = TimeSpan.FromSeconds(seconds);
                return ProviderErrorMapper.FromHttpStatus(azure.Status, operation, "Azure Blob", retryAfter, azure.ErrorCode);
            }
        }
        return ProviderErrorMapper.FromTransport(exception, operation, "Azure Blob")
            ?? StorageErrors.ProviderError($"{operation}: Azure Blob provider failed.", ProviderErrorMapper.ExceptionDetails(exception));
    }
}
