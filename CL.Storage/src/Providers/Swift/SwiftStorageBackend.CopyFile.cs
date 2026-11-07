using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CodeLogic.Core.Results;

namespace CL.Storage.Providers.Swift;

public sealed partial class SwiftStorageBackend
{

    /// <summary>Copies one object on the server, pinned to the version <paramref name="source"/> describes.</summary>
    private async Task<Result> CopyFileAsync(string sourcePath, string destinationPath, StorageItem source, StorageTransferOptions options, CancellationToken cancellationToken)
    {
        if (options.DestinationCondition is { IsEmpty: false })
            return Result.Failure(StorageErrors.Unsupported(
                "Swift has no destination condition on COPY, so a server-side copy cannot keep the destination condition."));
        if (options.ExpectedSourceETag is { } expected && !StagedWriter.SameETag(expected, source.ETag))
            return Result.Failure(StorageErrors.Conflict(
                $"The Swift source '{sourcePath}' changed since it was read.",
                $"expectedETag={expected.Trim('"')};actualETag={source.ETag}"));
        var destinationKey = ToKey(destinationPath);
        if (!options.Overwrite)
        {
            // Swift applies If-None-Match on COPY to the source, so the destination can only be checked first.
            using var head = await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Head, ObjectUri(destinationKey)),
                cancellationToken).ConfigureAwait(false);
            if (head.IsSuccessStatusCode) return Result.Failure(StorageErrors.Conflict("The Swift destination already exists."));
            if (head.StatusCode != HttpStatusCode.NotFound) return Result.Failure(FromStatus(head, "Check Swift copy destination"));
        }
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(new HttpMethod("COPY"), ObjectUri(ToKey(sourcePath), options.SourceVersionId));
            request.Headers.TryAddWithoutValidation("Destination", "/" + Uri.EscapeDataString(_configuration.Container) + "/" + EncodeObjectPath(destinationKey));
            // Applied to the source: the copy fails with 412 if the source is no longer the version read.
            if (source.ETag is not null) request.Headers.TryAddWithoutValidation("If-Match", QuoteETag(source.ETag));
            return request;
        }, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return Result.Success();
        return Result.Failure(response.StatusCode == HttpStatusCode.PreconditionFailed
            ? StorageErrors.Conflict($"The Swift source '{sourcePath}' changed since it was read.", "httpStatus=412")
            : FromStatus(response, "Copy Swift object"));
    }

    /// <summary>Compares an object with a condition the server would not enforce itself.</summary>
    private static Result CheckCondition(StorageItem current, StorageMutationCondition? condition, string operation)
    {
        if (condition is null or { IsEmpty: true }) return Result.Success();
        if (condition.ExpectedETag is { } etag && !StagedWriter.SameETag(etag, current.ETag))
            return Result.Failure(StorageErrors.Conflict($"The Swift object ETag no longer matches the requested {operation} condition."));
        if (condition.ExpectedVersionId is { } version && !string.Equals(version, current.VersionId, StringComparison.Ordinal))
            return Result.Failure(StorageErrors.Conflict($"The Swift object version no longer matches the requested {operation} condition."));
        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Head, ContainerUri()),
                cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? Result.Success()
                : Result.Failure(FromStatus(response, "Check Swift health"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Check Swift health")); }
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyDictionary<string, string>>> GetMetadataAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var info = await GetInfoAsync(path, cancellationToken).ConfigureAwait(false);
        return info.IsSuccess
            ? Result<IReadOnlyDictionary<string, string>>.Success(info.Value!.Metadata)
            : Result<IReadOnlyDictionary<string, string>>.Failure(info.Error!);
    }

    /// <inheritdoc />
    public async Task<Result<StorageItem>> SetMetadataAsync(
        string path,
        IReadOnlyDictionary<string, string> metadata,
        StorageMetadataUpdateOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        options ??= new StorageMetadataUpdateOptions();
        var validation = options.Validate(metadata);
        if (validation.IsFailure) return Result<StorageItem>.Failure(validation.Error!);
        if (options.ExpectedVersionId is not null)
            return Result<StorageItem>.Failure(StorageErrors.Unsupported(
                "This Swift endpoint does not expose a portable version condition for metadata updates."));
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result<StorageItem>.Failure(normalized.Error!);
        var snapshot = StorageMetadataSnapshot.Create(metadata);
        try
        {
            var info = await GetInfoAsync(normalized.Value!, cancellationToken).ConfigureAwait(false);
            if (info.IsFailure) return Result<StorageItem>.Failure(info.Error!);
            if (info.Value!.ItemType != StorageItemType.File)
                return Result<StorageItem>.Failure(StorageErrors.Conflict(
                    "Swift metadata can only be updated on an object."));
            // The If-Match sent below is not enforced by every Swift deployment: the ETag is compared here as well.
            if (options.ExpectedETag is { } expectedETag && !StagedWriter.SameETag(expectedETag, info.Value.ETag))
                return Result<StorageItem>.Failure(StorageErrors.Conflict(
                    "The Swift object ETag no longer matches the metadata update condition."));

            using var response = await SendAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, ObjectUri(ToKey(normalized.Value!)));
                if (options.Mode == StorageMetadataUpdateMode.Replace)
                    request.Headers.TryAddWithoutValidation("X-Fresh-Metadata", "True");
                if (options.ExpectedETag is not null)
                    request.Headers.TryAddWithoutValidation("If-Match", QuoteETag(options.ExpectedETag));
                foreach (var (name, value) in snapshot)
                    request.Headers.TryAddWithoutValidation("X-Object-Meta-" + name, value);
                return request;
            }, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Result<StorageItem>.Failure(FromStatus(response, "Update Swift metadata"));
            return await GetInfoAsync(normalized.Value!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Update Swift metadata")); }
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
            return Task.FromResult(Result<NativeConnectionLease<TClient>>.Failure(StorageErrors.Unsupported($"Swift does not expose native type '{typeof(TClient).FullName}'.")));
        return Task.FromResult(Result<NativeConnectionLease<TClient>>.Success(new NativeConnectionLease<TClient>(typed, _ => ValueTask.CompletedTask)));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _authenticationGate.Dispose();
            if (_ownsClient) _client.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    private async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead)
    {
        await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false);
        var request = requestFactory();
        request.Headers.TryAddWithoutValidation("X-Auth-Token", _token);
        var response = await _client.SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);
        var canRetry = request.Content is null;
        request.Dispose();
        if (response.StatusCode != HttpStatusCode.Unauthorized ||
            _configuration.AuthenticationMode == SwiftAuthenticationMode.StaticToken)
            return response;

        InvalidateAuthentication();
        if (!canRetry)
            return response;
        response.Dispose();
        await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false);
        request = requestFactory();
        request.Headers.TryAddWithoutValidation("X-Auth-Token", _token);
        response = await _client.SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);
        request.Dispose();
        return response;
    }

    private async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_token) && !string.IsNullOrWhiteSpace(_storageUrl) &&
            DateTimeOffset.UtcNow < _tokenExpiresAt - TimeSpan.FromMinutes(1))
            return;
        await _authenticationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrWhiteSpace(_token) && !string.IsNullOrWhiteSpace(_storageUrl) &&
                DateTimeOffset.UtcNow < _tokenExpiresAt - TimeSpan.FromMinutes(1))
                return;
            if (_configuration.AuthenticationMode == SwiftAuthenticationMode.TempAuthV1)
                await AuthenticateTempAuthAsync(cancellationToken).ConfigureAwait(false);
            else
                await AuthenticateKeystoneAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _authenticationGate.Release(); }
    }

    /// <summary>Authenticates with TempAuth v1: credentials in headers, token and storage URL in the reply.</summary>
    private async Task AuthenticateTempAuthAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _configuration.AuthenticationUrl);
        request.Headers.TryAddWithoutValidation("X-Auth-User", _configuration.Username);
        request.Headers.TryAddWithoutValidation("X-Auth-Key", _configuration.Password);
        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"TempAuth authentication failed with HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        if (!response.Headers.TryGetValues("X-Auth-Token", out var tokens))
            throw new InvalidOperationException("TempAuth did not return X-Auth-Token.");
        var storageUrl = _configuration.StorageUrl;
        if (string.IsNullOrWhiteSpace(storageUrl) && response.Headers.TryGetValues("X-Storage-Url", out var urls))
            storageUrl = CheckedStorageUrl(urls.First(), "TempAuth");
        var expires = response.Headers.TryGetValues("X-Auth-Token-Expires", out var lifetimes) &&
            long.TryParse(lifetimes.First(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                ? DateTimeOffset.UtcNow.AddSeconds(seconds)
                : DateTimeOffset.UtcNow.AddHours(1);
        _token = tokens.First();
        _storageUrl = storageUrl?.TrimEnd('/') ??
            throw new InvalidOperationException("TempAuth did not return X-Storage-Url.");
        _tokenExpiresAt = expires;
    }
}
