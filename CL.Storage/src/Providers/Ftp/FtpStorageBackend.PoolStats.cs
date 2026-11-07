using System.Diagnostics.CodeAnalysis;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CL.Storage.Providers.Local;
using CodeLogic.Core.Results;
using FluentFTP;
using FluentFTP.Exceptions;

namespace CL.Storage.Providers.Ftp;

public sealed partial class FtpStorageBackend
{

    internal ProviderPoolStats PoolStats => _clients.Stats;

    /// <summary>The session pool this backend uses, shared with registrations of the same settings.</summary>
    internal ProviderClientPool<AsyncFtpClient> Pool => _clients;

    /// <summary>Creates the session pool for a client factory; shared pools are created once per settings.</summary>
    internal static ProviderClientPool<AsyncFtpClient> CreatePool(
        Func<AsyncFtpClient> clientFactory,
        StorageSessionConfig? session,
        Func<AsyncFtpClient, CancellationToken, Task>? afterConnect) =>
        new(
            clientFactory,
            afterConnect is null
                ? static (client, token) => client.Connect(token)
                : async (client, token) =>
                {
                    await client.Connect(token).ConfigureAwait(false);
                    await afterConnect(client, token).ConfigureAwait(false);
                },
            static client => client.IsConnected,
            DestroyClientAsync,
            ProviderPoolOptions.From(session),
            ProbeAsync);

    /// <summary>Identifies the listing snapshots continuation tokens refer to; settings-based so tokens outlive a registration.</summary>
    internal string? ListingScope { get; init; }

    /// <summary>Records the certificate each session is offered; set by the factory.</summary>
    internal ServerIdentityRecorder? Identity { get; init; }

    StorageServerIdentity? IStorageDiagnosticsSource.PresentedIdentity => Identity?.Last;

    StorageSessionPoolStats? IStorageDiagnosticsSource.PoolStats => StorageEndpoints.ToPublic(_clients.Stats);

    async Task<Result<StorageServerDetails>> IStorageDiagnosticsSource.GetServerDetailsAsync(CancellationToken cancellationToken)
    {
        AsyncFtpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            var negotiated = new Dictionary<string, string>(StringComparer.Ordinal);
            if (client.IsEncrypted)
            {
                negotiated["tls"] = client.SslProtocol.ToString();
                if (client.SslCipherSuite is { } cipher)
                    negotiated["cipher"] = cipher.ToString();
            }
            var features = client.Capabilities
                .Where(capability => capability != FtpCapability.NONE)
                .Select(capability => capability.ToString())
                .Order(StringComparer.Ordinal)
                .ToArray();
            return Result<StorageServerDetails>.Success(new StorageServerDetails(
                string.IsNullOrWhiteSpace(client.SystemType) ? null : client.SystemType,
                client.ServerType == FtpServer.Unknown ? null : client.ServerType.ToString(),
                features,
                negotiated));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageServerDetails>.Failure(Fail(client, error, "Read FTP server details")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    /// <inheritdoc />
    public string ConnectionId { get; }
    /// <inheritdoc />
    public StorageProvider Provider => StorageProvider.Ftp;
    /// <inheritdoc />
    public string Root => _paths.Root;
    /// <inheritdoc />
    public StorageCapabilities Capabilities => AllowRawCommands
        ? new StorageCapabilities(FtpCapabilities.Features | StorageFeature.RawCommands, FtpCapabilities.Limits)
        : FtpCapabilities;

    /// <summary>Gets whether raw commands are allowed; off unless the connection enables <c>AllowRawCommands</c>.</summary>
    public bool AllowRawCommands { get; init; }

    /// <inheritdoc />
    public Task<Result<StorageItem>> GetInfoAsync(string path, CancellationToken cancellationToken = default) =>
        _retry.ExecuteAsync("Get FTP item info", RetryKind.Idempotent, (_, token) => GetInfoCoreAsync(path, token), cancellationToken);

    private async Task<Result<StorageItem>> GetInfoCoreAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure) return Result<StorageItem>.Failure(resolved.Error!);
        if (resolved.Value!.StoragePath.Length == 0)
            return Result<StorageItem>.Success(DirectoryItem(string.Empty));
        AsyncFtpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            var item = await FindAsync(client, resolved.Value.RemotePath, cancellationToken).ConfigureAwait(false);
            return item is null
                ? Result<StorageItem>.Failure(StorageErrors.NotFound($"FTP item '{resolved.Value.StoragePath}' was not found."))
                : Result<StorageItem>.Success(ToItem(resolved.Value.StoragePath, item));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageItem>.Failure(Fail(client, error, "Get FTP item info")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    /// <inheritdoc />
    public Task<Result<bool>> ExistsAsync(string path, CancellationToken cancellationToken = default) =>
        _retry.ExecuteAsync("Check FTP item existence", RetryKind.Idempotent, (_, token) => ExistsCoreAsync(path, token), cancellationToken);

    private async Task<Result<bool>> ExistsCoreAsync(string path, CancellationToken cancellationToken)
    {
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure) return Result<bool>.Failure(resolved.Error!);
        if (resolved.Value!.StoragePath.Length == 0) return Result<bool>.Success(true);
        AsyncFtpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            var exists = await client.FileExists(resolved.Value.RemotePath, cancellationToken).ConfigureAwait(false) ||
                await client.DirectoryExists(resolved.Value.RemotePath, cancellationToken).ConfigureAwait(false);
            return Result<bool>.Success(exists);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<bool>.Failure(Fail(client, error, "Check FTP item existence")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    /// <inheritdoc />
    public Task<Result<StoragePage>> ListAsync(string path, StorageListOptions? options = null, CancellationToken cancellationToken = default) =>
        _retry.ExecuteAsync("List FTP directory", RetryKind.Idempotent, (_, token) => ListCoreAsync(path, options, token), cancellationToken);

    private async Task<Result<StoragePage>> ListCoreAsync(string path, StorageListOptions? options, CancellationToken cancellationToken)
    {
        options ??= new StorageListOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<StoragePage>.Failure(validation.Error!);
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure) return Result<StoragePage>.Failure(resolved.Error!);
        var target = resolved.Value!;
        try
        {
            // GetListing returns the whole listing in one call, so it runs once per pass and every
            // later page is served from the cached snapshot instead of re-listing the directory.
            return await ProviderPaging.CreateAsync(
                ProviderPaging.Scope(ListingScope ?? ConnectionId, target.StoragePath, options.Recursive),
                options,
                async token =>
                {
                    AsyncFtpClient? listClient = null;
                    try
                    {
                        listClient = await OpenClientAsync(token).ConfigureAwait(false);
                        if (!await listClient.DirectoryExists(target.RemotePath, token).ConfigureAwait(false))
                            return Result<IEnumerable<StorageItem>>.Failure(
                                StorageErrors.NotFound($"FTP directory '{target.StoragePath}' was not found."));
                        var listing = options.Recursive
                            ? await listClient.GetListing(target.RemotePath, FtpListOption.Auto | FtpListOption.Recursive, token).ConfigureAwait(false)
                            : await listClient.GetListing(target.RemotePath, FtpListOption.Auto, token).ConfigureAwait(false);
                        var items = listing.Select(item =>
                            {
                                var relative = _paths.FromRemotePath(item.FullName);
                                return relative is null || relative.Length == 0 ? null : ToItem(relative, item);
                            })
                            .Where(item => item is not null)
                            .Cast<StorageItem>()
                            .ToArray();
                        return Result<IEnumerable<StorageItem>>.Success(items);
                    }
                    catch (Exception error) when (Observe(listClient, error, "List FTP directory")) { throw; }
                    finally { if (listClient is not null) await ReleaseClientAsync(listClient).ConfigureAwait(false); }
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StoragePage>.Failure(Map(error, "List FTP directory")); }
    }

    /// <inheritdoc />
    public Task<Result> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
        _retry.ExecuteAsync("Create FTP directory", RetryKind.Idempotent, (_, token) => CreateDirectoryCoreAsync(path, token), cancellationToken);

    private async Task<Result> CreateDirectoryCoreAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure) return Result.Failure(resolved.Error!);
        AsyncFtpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            await client.CreateDirectory(resolved.Value!.RemotePath, true, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Fail(client, error, "Create FTP directory")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    /// <inheritdoc />
    /// <remarks>Uploads are staged and renamed into place, so replaying a seekable source cannot leave a partial file.</remarks>
    public Task<Result<StorageItem>> UploadAsync(string path, Stream source, StorageUploadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (StorageTransferPipeline.Applies(this, options))
            return StorageTransferPipeline.UploadAsync(this, path, source, options, cancellationToken);
        return _retry.ExecuteUploadAsync("Upload FTP file", source, token => UploadCoreAsync(path, source, options, token), cancellationToken);
    }
}
