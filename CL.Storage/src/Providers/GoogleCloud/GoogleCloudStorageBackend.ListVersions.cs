using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CodeLogic.Core.Results;
using Google;
using Google.Cloud.Storage.V1;
using GcsObject = Google.Apis.Storage.v1.Data.Object;

namespace CL.Storage.Providers.GoogleCloud;

public sealed partial class GoogleCloudStorageBackend
{

    /// <inheritdoc />
    public async Task<Result<StorageVersionPage>> ListVersionsAsync(
        string path,
        StorageVersionListOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new StorageVersionListOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<StorageVersionPage>.Failure(validation.Error!);
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result<StorageVersionPage>.Failure(normalized.Error!);
        var key = ToKey(normalized.Value!);
        try
        {
            var providerPageSize = Math.Min(options.PageSize, 1_000);
            var listing = _client.ListObjectsAsync(_bucket, key, new ListObjectsOptions
            {
                Versions = true,
                PageSize = providerPageSize,
                PageToken = options.ContinuationToken
            });
            var page = await listing.ReadPageAsync(providerPageSize, cancellationToken).ConfigureAwait(false);
            var versions = new List<StorageVersion>();
            foreach (var version in page)
            {
                if (!string.Equals(version.Name, key, StringComparison.Ordinal))
                    continue;
                if (!version.Generation.HasValue)
                    return Result<StorageVersionPage>.Failure(StorageErrors.ProviderError(
                        "Google Cloud Storage returned a version without a generation."));
                versions.Add(new StorageVersion
                {
                    Path = normalized.Value!,
                    VersionId = version.Generation.Value.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    ETag = version.ETag,
                    Size = version.Size.HasValue ? checked((long)version.Size.Value) : null,
                    LastModified = ParseTimestamp(version.UpdatedRaw),
                    IsLatest = string.IsNullOrEmpty(version.TimeDeletedRaw),
                    IsDeleteMarker = false
                });
            }
            return Result<StorageVersionPage>.Success(new StorageVersionPage(
                versions.AsReadOnly(),
                page.NextPageToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageVersionPage>.Failure(Map(error, "List Google Cloud object versions")); }
    }

    /// <inheritdoc />
    public async Task<Result> DeleteVersionAsync(
        string path,
        string versionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var generation = ParseGeneration(versionId);
        if (generation.IsFailure) return Result.Failure(generation.Error!);
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result.Failure(normalized.Error!);
        try
        {
            await _client.DeleteObjectAsync(
                _bucket,
                ToKey(normalized.Value!),
                new DeleteObjectOptions { Generation = generation.Value },
                cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Delete Google Cloud object version")); }
    }

    /// <inheritdoc />
    public async Task<Result<StorageSignedUrl>> CreateSignedUrlAsync(
        string path,
        StorageSignedUrlOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new StorageSignedUrlOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<StorageSignedUrl>.Failure(validation.Error!);
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result<StorageSignedUrl>.Failure(normalized.Error!);
        if (_urlSigner is null)
            return Result<StorageSignedUrl>.Failure(StorageErrors.Unsupported(
                "The Google Cloud credential cannot sign URLs."));
        if (options.VersionId is not null)
            return Result<StorageSignedUrl>.Failure(StorageErrors.Unsupported(
                "Version-specific Google Cloud signed URLs require the native URL signer request template."));
        try
        {
            var expiresAt = DateTimeOffset.UtcNow.Add(options.ExpiresIn);
            var urlValue = await _urlSigner.SignAsync(
                _bucket,
                ToKey(normalized.Value!),
                options.ExpiresIn,
                options.Method == StorageSignedUrlMethod.Read ? HttpMethod.Get : HttpMethod.Put,
                signingVersion: null,
                cancellationToken).ConfigureAwait(false);
            if (!Uri.TryCreate(urlValue, UriKind.Absolute, out var url))
                return Result<StorageSignedUrl>.Failure(StorageErrors.ProviderError(
                    "The Google Cloud provider returned an invalid signed URL."));
            return Result<StorageSignedUrl>.Success(new StorageSignedUrl(url, options.Method, expiresAt));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageSignedUrl>.Failure(Map(error, "Create Google Cloud signed URL")); }
    }

    /// <inheritdoc />
    public bool TryGetNativeClient<TClient>([NotNullWhen(true)] out TClient? client) where TClient : class
    {
        client = _client as TClient;
        return client is not null;
    }

    /// <inheritdoc />
    public Task<Result<NativeConnectionLease<TClient>>> OpenNativeConnectionAsync<TClient>(CancellationToken cancellationToken = default) where TClient : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_client is not TClient typed)
            return Task.FromResult(Result<NativeConnectionLease<TClient>>.Failure(StorageErrors.Unsupported($"Google Cloud Storage does not expose native type '{typeof(TClient).FullName}'.")));
        return Task.FromResult(Result<NativeConnectionLease<TClient>>.Success(new NativeConnectionLease<TClient>(typed, _ => ValueTask.CompletedTask)));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_ownsClient && Interlocked.Exchange(ref _disposed, 1) == 0) _client.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task DownloadToPipeAsync(
        string bucket,
        string key,
        PipeWriter writer,
        DownloadObjectOptions? options,
        CancellationToken cancellationToken)
    {
        Exception? completionError = null;
        try
        {
            var destination = writer.AsStream(leaveOpen: true);
            await using var destinationLifetime = destination.ConfigureAwait(false);
            await _client.DownloadObjectAsync(
                bucket,
                key,
                destination,
                options,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            completionError = error is OperationCanceledException
                ? error
                : new IOException("The Google Cloud download stream failed.", error);
        }
        finally
        {
            try { await writer.CompleteAsync(completionError).ConfigureAwait(false); }
            catch { }
        }
    }

    private async Task<Result<StorageItem>> GetDirectoryInfoAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var _ in _client.ListObjectsAsync(_bucket, DirectoryPrefix(path), new ListObjectsOptions { PageSize = 1 })
                .WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                return Result<StorageItem>.Success(DirectoryItem(path));
            }
            return Result<StorageItem>.Failure(StorageErrors.NotFound($"Google Cloud object '{path}' was not found."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Get Google Cloud directory info")); }
    }

    private static string EncodeContinuationToken(GcsContinuationToken token) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(token)));

    private static Result<GcsContinuationToken> DecodeContinuationToken(string? token)
    {
        if (token is null)
            return Result<GcsContinuationToken>.Success(new GcsContinuationToken(null, 0));
        try
        {
            var decoded = JsonSerializer.Deserialize<GcsContinuationToken>(
                Encoding.UTF8.GetString(Convert.FromBase64String(token)));
            return decoded is not null && decoded.Skip >= 0
                ? Result<GcsContinuationToken>.Success(decoded)
                : Result<GcsContinuationToken>.Failure(StorageErrors.InvalidPath(
                    "The Google Cloud continuation token is invalid."));
        }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            return Result<GcsContinuationToken>.Failure(StorageErrors.InvalidPath(
                "The Google Cloud continuation token is invalid."));
        }
    }

    private string ToKey(string path) => _prefix + path;
    private string DirectoryPrefix(string path) => path.Length == 0 ? _prefix : ToKey(path).TrimEnd('/') + "/";
    /// <summary>The storage path of an object name under the mounted prefix, or null for a name the library cannot address (see <see cref="ObjectKeyPaths"/>).</summary>
    private string? FromKey(string key) => ObjectKeyPaths.FromKey(_prefix, key);
    private Result<string> Normalize(string path) => StoragePath.Normalize(path);
    private Result<string> NormalizeRequired(string path)
    {
        var normalized = Normalize(path);
        return normalized.IsFailure || normalized.Value!.Length > 0
            ? normalized
            : Result<string>.Failure(StorageErrors.InvalidPath("A non-root storage path is required."));
    }

    private static Result<long?> ParseGeneration(string? value)
    {
        if (value is null) return Result<long?>.Success(null);
        return long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var generation) && generation >= 0
            ? Result<long?>.Success(generation)
            : Result<long?>.Failure(StorageErrors.InvalidPath(
                "A Google Cloud version ID must be a non-negative numeric generation."));
    }
}
