using System.Runtime.CompilerServices;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Abstractions;

public static partial class StorageServiceExtensions
{

    /// <summary>
    /// Enumerates provider pages without buffering the complete listing. Provider-opaque continuation
    /// tokens are forwarded unchanged and repeated tokens terminate with a failed result.
    /// </summary>
    public static async IAsyncEnumerable<Result<StoragePage>> EnumeratePagesAsync(
        this IStorageService storage,
        string path,
        StorageListOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        options ??= new StorageListOptions();
        var validation = options.Validate();
        if (validation.IsFailure)
        {
            yield return Result<StoragePage>.Failure(validation.Error!);
            yield break;
        }

        var continuationToken = options.ContinuationToken;
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(continuationToken))
            seenTokens.Add(continuationToken);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await storage.ListAsync(
                path,
                options with { ContinuationToken = continuationToken },
                cancellationToken).ConfigureAwait(false);
            yield return page;
            if (page.IsFailure)
                yield break;

            var next = page.Value!.ContinuationToken;
            if (string.IsNullOrEmpty(next))
                yield break;
            if (!seenTokens.Add(next))
            {
                yield return Result<StoragePage>.Failure(StorageErrors.ProviderError(
                    "The storage provider repeated a continuation token while enumerating pages."));
                yield break;
            }
            continuationToken = next;
        }
    }

    /// <summary>
    /// Enumerates items one at a time. A page failure is emitted as one failed item result and ends
    /// enumeration, allowing callers to retain items from earlier successful pages.
    /// </summary>
    public static async IAsyncEnumerable<Result<StorageItem>> EnumerateItemsAsync(
        this IStorageService storage,
        string path,
        StorageListOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var page in storage.EnumeratePagesAsync(path, options, cancellationToken)
            .ConfigureAwait(false))
        {
            if (page.IsFailure)
            {
                yield return Result<StorageItem>.Failure(page.Error!);
                yield break;
            }
            foreach (var item in page.Value!.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return Result<StorageItem>.Success(item);
            }
        }
    }

    /// <summary>Gets item information concurrently while preserving the input order and per-item results.</summary>
    public static async Task<Result<IReadOnlyList<StorageBatchItemResult<StorageItem>>>> GetInfoBatchAsync(
        this IStorageService storage,
        IEnumerable<string> paths,
        StorageBatchOptions? batchOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(paths);
        batchOptions ??= new StorageBatchOptions();
        var inputs = paths.ToArray();
        var validation = ValidateBatch(batchOptions, inputs.Length);
        if (validation.IsFailure)
            return Result<IReadOnlyList<StorageBatchItemResult<StorageItem>>>.Failure(validation.Error!);

        var results = new StorageBatchItemResult<StorageItem>[inputs.Length];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, inputs.Length),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = batchOptions.MaxConcurrency,
                CancellationToken = cancellationToken
            },
            async (index, token) =>
            {
                Result<StorageItem> item;
                try
                {
                    item = await storage.GetInfoAsync(inputs[index], token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    item = Result<StorageItem>.Failure(StorageErrors.FromException(error, "Get batch item info"));
                }
                results[index] = new StorageBatchItemResult<StorageItem>(index, inputs[index], item);
            }).ConfigureAwait(false);
        return Result<IReadOnlyList<StorageBatchItemResult<StorageItem>>>.Success(Array.AsReadOnly(results));
    }

    /// <summary>Deletes paths concurrently while preserving the input order and per-item results.</summary>
    public static async Task<Result<IReadOnlyList<StorageBatchMutationResult>>> DeleteBatchAsync(
        this IStorageService storage,
        IEnumerable<string> paths,
        StorageDeleteOptions? deleteOptions = null,
        StorageBatchOptions? batchOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(paths);
        deleteOptions ??= new StorageDeleteOptions();
        batchOptions ??= new StorageBatchOptions();
        var inputs = paths.ToArray();
        var validation = ValidateBatch(batchOptions, inputs.Length);
        if (validation.IsFailure)
            return Result<IReadOnlyList<StorageBatchMutationResult>>.Failure(validation.Error!);
        validation = deleteOptions.Validate();
        if (validation.IsFailure)
            return Result<IReadOnlyList<StorageBatchMutationResult>>.Failure(validation.Error!);

        var results = new StorageBatchMutationResult[inputs.Length];
        await RunMutationBatchAsync(
            inputs.Length,
            batchOptions,
            cancellationToken,
            async (index, token) =>
            {
                Result result;
                try
                {
                    result = await storage.DeleteAsync(inputs[index], deleteOptions, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    result = Result.Failure(StorageErrors.FromException(error, "Delete batch item"));
                }
                results[index] = new StorageBatchMutationResult(index, inputs[index], null, result);
            }).ConfigureAwait(false);
        return Result<IReadOnlyList<StorageBatchMutationResult>>.Success(Array.AsReadOnly(results));
    }

    /// <summary>Copies path pairs concurrently while preserving the input order and per-item results.</summary>
    public static Task<Result<IReadOnlyList<StorageBatchMutationResult>>> CopyBatchAsync(
        this IStorageService storage,
        IEnumerable<StorageTransferRequest> transfers,
        StorageTransferOptions? transferOptions = null,
        StorageBatchOptions? batchOptions = null,
        CancellationToken cancellationToken = default) =>
        TransferBatchAsync(storage, transfers, transferOptions, batchOptions, move: false, cancellationToken);

    /// <summary>Moves path pairs concurrently while preserving the input order and per-item results.</summary>
    public static Task<Result<IReadOnlyList<StorageBatchMutationResult>>> MoveBatchAsync(
        this IStorageService storage,
        IEnumerable<StorageTransferRequest> transfers,
        StorageTransferOptions? transferOptions = null,
        StorageBatchOptions? batchOptions = null,
        CancellationToken cancellationToken = default) =>
        TransferBatchAsync(storage, transfers, transferOptions, batchOptions, move: true, cancellationToken);

    private static async Task<Result<IReadOnlyList<StorageBatchMutationResult>>> TransferBatchAsync(
        IStorageService storage,
        IEnumerable<StorageTransferRequest> transfers,
        StorageTransferOptions? transferOptions,
        StorageBatchOptions? batchOptions,
        bool move,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(transfers);
        transferOptions ??= new StorageTransferOptions();
        batchOptions ??= new StorageBatchOptions();
        var inputs = transfers.ToArray();
        var validation = ValidateBatch(batchOptions, inputs.Length);
        if (validation.IsFailure)
            return Result<IReadOnlyList<StorageBatchMutationResult>>.Failure(validation.Error!);
        validation = transferOptions.Validate();
        if (validation.IsFailure)
            return Result<IReadOnlyList<StorageBatchMutationResult>>.Failure(validation.Error!);
        if (inputs.Any(input => input is null))
            return Result<IReadOnlyList<StorageBatchMutationResult>>.Failure(StorageErrors.InvalidPath(
                "A transfer batch cannot contain a null request."));

        var results = new StorageBatchMutationResult[inputs.Length];
        await RunMutationBatchAsync(
            inputs.Length,
            batchOptions,
            cancellationToken,
            async (index, token) =>
            {
                var input = inputs[index];
                Result result;
                try
                {
                    result = move
                        ? await storage.MoveAsync(
                            input.SourcePath,
                            input.DestinationPath,
                            transferOptions,
                            token).ConfigureAwait(false)
                        : await storage.CopyAsync(
                            input.SourcePath,
                            input.DestinationPath,
                            transferOptions,
                            token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    result = Result.Failure(StorageErrors.FromException(
                        error,
                        move ? "Move batch item" : "Copy batch item"));
                }
                results[index] = new StorageBatchMutationResult(
                    index,
                    input.SourcePath,
                    input.DestinationPath,
                    result);
            }).ConfigureAwait(false);
        return Result<IReadOnlyList<StorageBatchMutationResult>>.Success(Array.AsReadOnly(results));
    }
}
