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

/// <summary>Root-scoped storage over FTP, explicit FTPS, or implicit FTPS.</summary>
public sealed class FtpStorageBackend : IStorageBackend, IStorageAttributeService, IStorageChecksumService, IStorageAppendService, IStorageCommandService, IStorageSpaceService, IStorageDiagnosticsSource, IStorageRestoringReplace
{
    private static readonly StorageCapabilities FtpCapabilities = new(
        StorageFeature.PhysicalDirectories |
        StorageFeature.FileCopy |
        StorageFeature.FileMove |
        StorageFeature.DirectoryMove |
        StorageFeature.RelayedCopy |
        StorageFeature.ServerSideMove |
        // A rename on the server (RNFR/RNTO) moves a whole tree in one step,
        // so folder moves no longer fall back to copy-then-delete through the client.
        StorageFeature.AtomicMove |
        StorageFeature.RangeReads |
        StorageFeature.SpaceInfo |
        StorageFeature.Append |
        StorageFeature.Checksums |
        StorageFeature.Links |
        StorageFeature.Permissions |
        StorageFeature.SetTimestamps |
        StorageFeature.ReadLinks);

    private readonly ProviderClientPool<AsyncFtpClient> _clients;
    private readonly IPoolHandle<AsyncFtpClient> _pool;
    private readonly Action? _sessionOpened;
    private readonly RemotePathResolver _paths;
    private readonly long _maxBufferedDownloadBytes;
    private readonly ProviderRetryPolicy _retry;
    private readonly IStorageConnectionObserver? _observer;

    /// <summary>Initializes a backend that leases FTP clients from a factory.</summary>
    /// <param name="connectionId">Unique connection ID exposed by the storage registry.</param>
    /// <param name="clientFactory">Factory that creates configured FTP clients.</param>
    /// <param name="root">Optional remote directory mounted as the connection root.</param>
    /// <param name="maxBufferedDownloadBytes">Maximum size accepted by buffered download helpers.</param>
    /// <param name="session">Session pool limits; defaults when omitted.</param>
    /// <param name="retry">Transient-failure retry policy; defaults when omitted.</param>
    public FtpStorageBackend(
        string connectionId,
        Func<AsyncFtpClient> clientFactory,
        string? root = null,
        long maxBufferedDownloadBytes = 67_108_864,
        StorageSessionConfig? session = null,
        StorageRetryConfig? retry = null)
        : this(connectionId, clientFactory, root, maxBufferedDownloadBytes, session, retry, observer: null)
    {
    }

    internal FtpStorageBackend(
        string connectionId,
        Func<AsyncFtpClient> clientFactory,
        string? root,
        long maxBufferedDownloadBytes,
        StorageSessionConfig? session,
        StorageRetryConfig? retry,
        IStorageConnectionObserver? observer,
        Func<AsyncFtpClient, CancellationToken, Task>? afterConnect = null)
        : this(connectionId, new OwnedPool<AsyncFtpClient>(CreatePool(clientFactory, session, afterConnect)), root, maxBufferedDownloadBytes, retry, observer)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
    }

    internal FtpStorageBackend(
        string connectionId,
        IPoolHandle<AsyncFtpClient> pool,
        string? root,
        long maxBufferedDownloadBytes,
        StorageRetryConfig? retry,
        IStorageConnectionObserver? observer)
    {
        if (string.IsNullOrWhiteSpace(connectionId)) throw new ArgumentException("Connection ID is required.", nameof(connectionId));
        if (maxBufferedDownloadBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBufferedDownloadBytes));
        ConnectionId = connectionId;
        _observer = observer;
        _pool = pool;
        _clients = pool.Pool;
        if (observer is not null)
        {
            // Reported per rent, not through the pool's event: a shared pool serves other registrations too.
            _sessionOpened = () => observer.SessionOpened(connectionId, StorageProvider.Ftp);
        }
        _retry = new ProviderRetryPolicy(retry, connectionId, StorageProvider.Ftp, observer);
        _retry.Enrich = (error, attempt) => TlsDiagnosis.Enrich(error, Identity, attempt);
        _paths = new RemotePathResolver(root);
        _maxBufferedDownloadBytes = maxBufferedDownloadBytes;
    }

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

    private async Task<Result<StorageItem>> UploadCoreAsync(string path, Stream source, StorageUploadOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new StorageUploadOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<StorageItem>.Failure(validation.Error!);
        if (options.Condition is { IsEmpty: false })
            return Result<StorageItem>.Failure(StorageErrors.Unsupported(
                "FTP does not expose portable atomic upload conditions."));
        if (options.Metadata.Count > 0)
            return Result<StorageItem>.Failure(StorageErrors.Unsupported(
                "FTP does not support portable user metadata."));
        var resolved = _paths.Resolve(path, requireNonRoot: true);
        if (resolved.IsFailure) return Result<StorageItem>.Failure(resolved.Error!);
        AsyncFtpClient? client = null;
        string? stagingPath = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            var parent = RemotePathResolver.Parent(resolved.Value!.RemotePath);
            if (options.CreateParents)
                await client.CreateDirectory(parent, true, cancellationToken).ConfigureAwait(false);
            else if (!await client.DirectoryExists(parent, cancellationToken).ConfigureAwait(false))
                return Result<StorageItem>.Failure(StorageErrors.NotFound("The FTP destination parent directory was not found."));
            var existing = await StatAsync(client, resolved.Value.RemotePath, cancellationToken).ConfigureAwait(false);
            if (existing?.Type == FtpObjectType.Directory)
                return Result<StorageItem>.Failure(StorageErrors.Conflict("The FTP upload destination is a directory."));
            if (existing is not null && !options.Overwrite)
                return Result<StorageItem>.Failure(StorageErrors.Conflict("The FTP destination already exists."));

            var staging = await AllocateTemporaryPathAsync(client, parent, "upload", cancellationToken).ConfigureAwait(false);
            if (staging.IsFailure)
                return Result<StorageItem>.Failure(staging.Error!);
            stagingPath = staging.Value!;
            var status = await client.UploadStream(
                source,
                stagingPath,
                FtpRemoteExists.Skip,
                createRemoteDir: false,
                progress: null,
                cancellationToken).ConfigureAwait(false);
            if (status == FtpStatus.Skipped)
                return Result<StorageItem>.Failure(StorageErrors.Conflict("The FTP destination already exists."));
            if (status != FtpStatus.Success)
                return Result<StorageItem>.Failure(StorageErrors.ProviderError("The FTP server did not accept the upload."));
            var committed = await CommitPathAsync(
                client,
                stagingPath,
                resolved.Value.RemotePath,
                FtpObjectType.File,
                options.Overwrite,
                cancellationToken).ConfigureAwait(false);
            if (committed.IsFailure)
            {
                // Committed, but the replaced file's backup stayed: the staging file is the destination now.
                if (StorageErrorInfo.DestinationCommitted(committed.Error)) stagingPath = null;
                return Result<StorageItem>.Failure(committed.Error!);
            }
            stagingPath = null;
            var item = await StatAsync(client, resolved.Value.RemotePath, cancellationToken).ConfigureAwait(false);
            return item is null
                ? Result<StorageItem>.Success(FileItem(resolved.Value.StoragePath, source.CanSeek ? source.Length : null))
                : Result<StorageItem>.Success(ToItem(resolved.Value.StoragePath, item));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageItem>.Failure(Fail(client, error, "Upload FTP file")); }
        finally
        {
            if (client is not null && stagingPath is not null)
                await TryDeletePathAsync(client, stagingPath).ConfigureAwait(false);
            if (client is not null)
                await ReleaseClientAsync(client).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<Result<StorageItem>> UploadBytesAsync(string path, byte[] content, StorageUploadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        await using var source = new MemoryStream(content, writable: false);
        return await UploadAsync(path, source, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<Stream>> DownloadAsync(string path, StorageDownloadOptions? options = null, CancellationToken cancellationToken = default) =>
        await StorageTransferPipeline.MeterAsync(this, path, await DownloadUnmeteredAsync(path, options, cancellationToken).ConfigureAwait(false), options, cancellationToken).ConfigureAwait(false);

    private Task<Result<Stream>> DownloadUnmeteredAsync(string path, StorageDownloadOptions? options, CancellationToken cancellationToken) =>
        _retry.ExecuteAsync("Download FTP file", RetryKind.Idempotent, (_, token) => DownloadCoreAsync(path, options, token), cancellationToken);

    private async Task<Result<Stream>> DownloadCoreAsync(string path, StorageDownloadOptions? options, CancellationToken cancellationToken)
    {
        options ??= new StorageDownloadOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<Stream>.Failure(validation.Error!);
        if (options.VersionId is not null)
            return Result<Stream>.Failure(StorageErrors.Unsupported(
                "FTP does not support version-specific downloads."));
        var resolved = _paths.Resolve(path, requireNonRoot: true);
        if (resolved.IsFailure) return Result<Stream>.Failure(resolved.Error!);
        AsyncFtpClient? client = null;
        var ownershipTransferred = false;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            var item = await StatAsync(client, resolved.Value!.RemotePath, cancellationToken).ConfigureAwait(false);
            if (item is null) return Result<Stream>.Failure(StorageErrors.NotFound($"FTP file '{resolved.Value.StoragePath}' was not found."));
            if (item.Type == FtpObjectType.Directory) return Result<Stream>.Failure(StorageErrors.Conflict("An FTP directory cannot be downloaded as a file."));
            if (item.Size >= 0 && options.Offset > item.Size)
                return Result<Stream>.Failure(StorageErrors.InvalidPath("The range offset exceeds the FTP file length."));
            Stream stream = await client.OpenRead(
                resolved.Value.RemotePath,
                client.Config.DownloadDataType,
                options.Offset,
                checkIfFileExists: true,
                cancellationToken).ConfigureAwait(false);
            if (options.Length.HasValue)
                stream = new RangeReadStream(stream, item.Size >= 0
                    ? Math.Min(options.Length.Value, Math.Max(0, item.Size - options.Offset))
                    : options.Length.Value);
            var owned = new AsyncOwnedResourceStream(stream, () => ReleaseClientAsync(client), () => MarkFaulted(client, "Download FTP file"));
            ownershipTransferred = true;
            return Result<Stream>.Success(owned);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<Stream>.Failure(Fail(client, error, "Download FTP file")); }
        finally { if (client is not null && !ownershipTransferred) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    /// <inheritdoc />
    public async Task<Result<byte[]>> DownloadBytesAsync(string path, StorageDownloadOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new StorageDownloadOptions();
        var limit = options.MaxBufferedBytes ?? _maxBufferedDownloadBytes;
        var download = await DownloadAsync(path, options, cancellationToken).ConfigureAwait(false);
        if (download.IsFailure) return Result<byte[]>.Failure(download.Error!);
        await using var source = download.Value!;
        using var destination = new MemoryStream();
        var buffer = new byte[81_920];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (destination.Length > Math.Min(limit, int.MaxValue) - read)
                return Result<byte[]>.Failure(StorageErrors.TooLarge($"The download exceeds the {limit} byte buffering limit."));
            destination.Write(buffer, 0, read);
        }
        return Result<byte[]>.Success(destination.ToArray());
    }

    /// <inheritdoc />
    public Task<Result> DeleteAsync(string path, StorageDeleteOptions? options = null, CancellationToken cancellationToken = default) =>
        _retry.ExecuteAsync("Delete FTP item", RetryKind.NonIdempotent, (_, token) => DeleteCoreAsync(path, options, token), cancellationToken);

    private async Task<Result> DeleteCoreAsync(string path, StorageDeleteOptions? options, CancellationToken cancellationToken)
    {
        options ??= new StorageDeleteOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return validation;
        if (options.Condition is { IsEmpty: false })
            return Result.Failure(StorageErrors.Unsupported(
                "FTP does not expose portable atomic delete conditions."));
        var resolved = _paths.Resolve(path, requireNonRoot: true);
        if (resolved.IsFailure) return Result.Failure(resolved.Error!);
        AsyncFtpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            var item = await FindAsync(client, resolved.Value!.RemotePath, cancellationToken).ConfigureAwait(false);
            if (item is null) return options.IgnoreMissing
                ? Result.Success()
                : Result.Failure(StorageErrors.NotFound($"FTP item '{resolved.Value.StoragePath}' was not found."));
            if (item.Type == FtpObjectType.Directory)
            {
                if (!options.Recursive)
                    return await RemoveEmptyDirectoryAsync(client, resolved.Value, cancellationToken).ConfigureAwait(false);
                else
                {
                    await client.DeleteDirectory(resolved.Value.RemotePath, FtpListOption.Recursive | FtpListOption.AllFiles, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                await client.DeleteFile(resolved.Value.RemotePath, cancellationToken).ConfigureAwait(false);
            }
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Fail(client, error, "Delete FTP item")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    /// <summary>
    /// Defence in depth for arguments spliced into a raw FTP command: the control connection is line based, so a
    /// control character (CR/LF above all) would end the command and start another. <see cref="StoragePath.Normalize"/>
    /// already rejects them; this keeps a raw command safe if a path ever reaches it by another route.
    /// </summary>
    internal static Error? RawArgumentError(string argument) =>
        StoragePath.IndexOfControlCharacter(argument) >= 0
            ? StorageErrors.InvalidPath("An FTP command argument cannot contain control characters.")
            : null;

    /// <summary>
    /// Removes a directory only if it is empty, with a raw <c>RMD</c>: the server itself refuses a directory that
    /// holds anything, hidden names included, so there is no window between a check and the removal. (FluentFTP's
    /// <c>DeleteDirectory</c> deletes the contents first, and its listing can miss names LIST hides.) When the server
    /// refuses, the directory is listed with hidden names to tell "not empty" from other refusals.
    /// </summary>
    private static async Task<Result> RemoveEmptyDirectoryAsync(AsyncFtpClient client, ResolvedRemotePath resolved, CancellationToken cancellationToken)
    {
        if (RawArgumentError(resolved.RemotePath) is { } invalid) return Result.Failure(invalid);
        var reply = await client.Execute($"RMD {resolved.RemotePath}", cancellationToken).ConfigureAwait(false);
        if (reply.Success) return Result.Success();
        FtpListItem[] children;
        try
        {
            children = await client.GetListing(resolved.RemotePath, FtpListOption.Auto | FtpListOption.AllFiles, cancellationToken).ConfigureAwait(false);
        }
        catch (FtpCommandException)
        {
            // A server that rejects "LIST -a" still lists what it shows.
            children = await client.GetListing(resolved.RemotePath, FtpListOption.Auto, cancellationToken).ConfigureAwait(false);
        }
        if (children.Any(child => child.Name is not ("." or "..")))
            return Result.Failure(StorageErrors.Conflict("The FTP directory is not empty.", $"{StorageErrorInfo.FtpReplyKey}={reply.Code}"));
        return Result.Failure(MapReply(new FtpCommandException(reply), "Delete FTP directory"));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Relayed through the client: FTP has no server-side copy, and a download and an upload need two sessions.
    /// FTP items have no ETag or version, so source pinning and destination conditions are unsupported.
    /// </remarks>
    public async Task<Result> CopyAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new StorageTransferOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return validation;
        if (Unpinnable(options) is { } unsupported) return unsupported;
        var source = _paths.Resolve(sourcePath, requireNonRoot: true);
        if (source.IsFailure) return Result.Failure(source.Error!);
        var destination = _paths.Resolve(destinationPath, requireNonRoot: true);
        if (destination.IsFailure) return Result.Failure(destination.Error!);
        var relationship = StorageTransferPath.ValidateDistinct(
            source.Value!.StoragePath,
            destination.Value!.StoragePath);
        if (relationship.IsFailure) return relationship;
        var info = await GetInfoAsync(source.Value.StoragePath, cancellationToken).ConfigureAwait(false);
        if (info.IsFailure) return Result.Failure(info.Error!);
        if (info.Value!.ItemType == StorageItemType.Directory)
        {
            relationship = StorageTransferPath.ValidateDirectoryDestination(
                source.Value.StoragePath,
                destination.Value.StoragePath);
            if (relationship.IsFailure) return relationship;
            return Result.Failure(StorageErrors.Unsupported("FTP directory copy is not supported."));
        }
        var download = await DownloadAsync(source.Value.StoragePath, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (download.IsFailure) return Result.Failure(download.Error!);
        await using var stream = download.Value!;
        var upload = await UploadAsync(destination.Value.StoragePath, stream, new StorageUploadOptions
        {
            Overwrite = options.Overwrite,
            CreateParents = options.CreateParents
        }, cancellationToken).ConfigureAwait(false);
        return upload.IsSuccess ? Result.Success() : Result.Failure(upload.Error!);
    }

    /// <summary>FTP items have no ETag or version, so a copy or move cannot be pinned to one or conditioned on one.</summary>
    private static Result? Unpinnable(StorageTransferOptions options) =>
        options.ExpectedSourceETag is not null || options.SourceVersionId is not null || options.DestinationCondition is { IsEmpty: false }
            ? Result.Failure(StorageErrors.Unsupported("FTP cannot pin a copy or move to a source version or keep a destination condition."))
            : (Result?)null;

    /// <inheritdoc />
    public Task<Result> MoveAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default) =>
        _retry.ExecuteAsync("Move FTP item", RetryKind.NonIdempotent, (_, token) => MoveCoreAsync(sourcePath, destinationPath, options, token), cancellationToken);

    private async Task<Result> MoveCoreAsync(string sourcePath, string destinationPath, StorageTransferOptions? options, CancellationToken cancellationToken)
    {
        options ??= new StorageTransferOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return validation;
        if (Unpinnable(options) is { } unsupported) return unsupported;
        var source = _paths.Resolve(sourcePath, requireNonRoot: true);
        if (source.IsFailure) return Result.Failure(source.Error!);
        var destination = _paths.Resolve(destinationPath, requireNonRoot: true);
        if (destination.IsFailure) return Result.Failure(destination.Error!);
        var relationship = StorageTransferPath.ValidateDistinct(
            source.Value!.StoragePath,
            destination.Value!.StoragePath);
        if (relationship.IsFailure) return relationship;
        AsyncFtpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            var item = await StatAsync(client, source.Value!.RemotePath, cancellationToken).ConfigureAwait(false);
            if (item is null) return Result.Failure(StorageErrors.NotFound($"FTP item '{source.Value.StoragePath}' was not found."));
            if (item.Type == FtpObjectType.Directory)
            {
                relationship = StorageTransferPath.ValidateDirectoryDestination(
                    source.Value.StoragePath,
                    destination.Value!.StoragePath);
                if (relationship.IsFailure) return relationship;
            }
            if (options.CreateParents)
                await client.CreateDirectory(RemotePathResolver.Parent(destination.Value!.RemotePath), true, cancellationToken).ConfigureAwait(false);
            else if (!await client.DirectoryExists(RemotePathResolver.Parent(destination.Value!.RemotePath), cancellationToken).ConfigureAwait(false))
                return Result.Failure(StorageErrors.NotFound("The FTP destination parent directory was not found."));
            return await CommitPathAsync(
                client,
                source.Value.RemotePath,
                destination.Value!.RemotePath,
                item.Type,
                options.Overwrite,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Fail(client, error, "Move FTP item")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    /// <inheritdoc />
    public Task<Result> CheckHealthAsync(CancellationToken cancellationToken = default) =>
        _retry.ExecuteAsync("Check FTP health", RetryKind.Idempotent, (_, token) => CheckHealthCoreAsync(token), cancellationToken);

    private async Task<Result> CheckHealthCoreAsync(CancellationToken cancellationToken)
    {
        AsyncFtpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            return await client.DirectoryExists(_paths.RemoteRoot, cancellationToken).ConfigureAwait(false)
                ? Result.Success()
                : Result.Failure(StorageErrors.NotFound("The configured FTP root was not found."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Fail(client, error, "Check FTP health")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    /// <inheritdoc />
    public bool TryGetNativeClient<TClient>([NotNullWhen(true)] out TClient? client) where TClient : class
    {
        client = null;
        return false;
    }

    /// <inheritdoc />
    public async Task<Result<NativeConnectionLease<TClient>>> OpenNativeConnectionAsync<TClient>(CancellationToken cancellationToken = default) where TClient : class
    {
        if (typeof(TClient) != typeof(AsyncFtpClient))
            return Result<NativeConnectionLease<TClient>>.Failure(StorageErrors.Unsupported($"FTP does not expose native type '{typeof(TClient).FullName}'."));
        AsyncFtpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            var typed = (TClient)(object)client;
            client = null;
            return Result<NativeConnectionLease<TClient>>.Success(new NativeConnectionLease<TClient>(
                typed,
                value => _clients.DiscardAsync((AsyncFtpClient)(object)value)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<NativeConnectionLease<TClient>>.Failure(Fail(client, error, "Open native FTP connection")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    /// <inheritdoc />
    /// <remarks>Uses <c>SITE CHMOD</c>, which Unix-style servers implement and Windows servers usually do not.</remarks>
    public Task<Result> SetPermissionsAsync(string path, int unixMode, CancellationToken cancellationToken = default) =>
        WithClientAsync(path, "Set FTP permissions", RetryKind.Idempotent, (client, remote, token) =>
            client.Chmod(remote, UnixPermissions.ToOctalDigits(unixMode), token), cancellationToken);

    /// <inheritdoc />
    public Task<Result> SetOwnerAsync(string path, long? ownerId, long? groupId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure(StorageErrors.Unsupported("FTP has no command for changing file ownership.")));

    /// <inheritdoc />
    /// <remarks>Uses <c>MFMT</c> (or the two-argument <c>MDTM</c>); the access time cannot be set over FTP and is ignored.</remarks>
    public Task<Result> SetTimestampsAsync(string path, DateTimeOffset? lastModified, DateTimeOffset? lastAccessed = null, CancellationToken cancellationToken = default) =>
        lastModified is not { } modified
            ? Task.FromResult(Result.Failure(StorageErrors.Unsupported("FTP can only set the modification time.")))
            : WithClientAsync(path, "Set FTP timestamps", RetryKind.Idempotent, (client, remote, token) =>
                client.SetModifiedTime(remote, modified.UtcDateTime, token), cancellationToken);

    /// <inheritdoc />
    public Task<Result> CreateLinkAsync(string linkPath, string targetPath, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure(StorageErrors.Unsupported("FTP has no standard command for creating links.")));

    /// <inheritdoc />
    public Task<Result<StorageLinkInfo>> ReadLinkAsync(string path, CancellationToken cancellationToken = default) =>
        _retry.ExecuteAsync("Read FTP link", RetryKind.Idempotent, async (_, token) =>
        {
            var resolved = _paths.Resolve(path, requireNonRoot: true);
            if (resolved.IsFailure) return Result<StorageLinkInfo>.Failure(resolved.Error!);
            AsyncFtpClient? client = null;
            try
            {
                client = await OpenClientAsync(token).ConfigureAwait(false);
                // The link entry itself is only visible in its parent's listing; stat-style calls follow it.
                var parent = RemotePathResolver.Parent(resolved.Value!.RemotePath);
                var listing = await client.GetListing(parent, FtpListOption.Auto, token).ConfigureAwait(false);
                var entry = listing.FirstOrDefault(item => _paths.FromRemotePath(item.FullName) == resolved.Value.StoragePath);
                if (entry is null)
                    return Result<StorageLinkInfo>.Failure(StorageErrors.NotFound($"FTP item '{resolved.Value.StoragePath}' was not found."));
                if (entry.Type != FtpObjectType.Link || string.IsNullOrWhiteSpace(entry.LinkTarget))
                    return Result<StorageLinkInfo>.Failure(StorageErrors.Conflict($"FTP item '{resolved.Value.StoragePath}' is not a link."));
                return Result<StorageLinkInfo>.Success(LinkInfo(entry.LinkTarget, parent));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) { return Result<StorageLinkInfo>.Failure(Fail(client, error, "Read FTP link")); }
            finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
        }, cancellationToken);

    /// <inheritdoc />
    /// <remarks>Uses <c>HASH</c> or the <c>XMD5</c>/<c>XSHA256</c>/<c>XSHA512</c> commands when the server advertises them.</remarks>
    public Task<Result<StorageChecksum>> GetServerChecksumAsync(string path, StorageChecksumAlgorithm algorithm, CancellationToken cancellationToken = default)
    {
        var ftpAlgorithm = algorithm switch
        {
            StorageChecksumAlgorithm.Md5 => FtpHashAlgorithm.MD5,
            StorageChecksumAlgorithm.Sha256 => FtpHashAlgorithm.SHA256,
            StorageChecksumAlgorithm.Sha512 => FtpHashAlgorithm.SHA512,
            _ => FtpHashAlgorithm.NONE
        };
        if (ftpAlgorithm == FtpHashAlgorithm.NONE)
            return Task.FromResult(ProviderChecksums.Unavailable(algorithm, $"FTP has no command for {algorithm} checksums."));
        return _retry.ExecuteAsync("Get FTP checksum", RetryKind.Idempotent, async (_, token) =>
        {
            var resolved = _paths.Resolve(path, requireNonRoot: true);
            if (resolved.IsFailure) return Result<StorageChecksum>.Failure(resolved.Error!);
            AsyncFtpClient? client = null;
            try
            {
                client = await OpenClientAsync(token).ConfigureAwait(false);
                if ((client.HashAlgorithms & ftpAlgorithm) == 0)
                    return ProviderChecksums.Unavailable(algorithm, $"The FTP server does not offer {algorithm} checksums.");
                var hash = await client.GetChecksum(resolved.Value!.RemotePath, ftpAlgorithm, token).ConfigureAwait(false);
                return hash.IsValid && hash.Algorithm == ftpAlgorithm
                    ? ProviderChecksums.FromHex(algorithm, hash.Value)
                    : ProviderChecksums.Unavailable(algorithm);
            }
            catch (FtpHashUnsupportedException)
            {
                return ProviderChecksums.Unavailable(algorithm, $"The FTP server does not offer {algorithm} checksums.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) { return Result<StorageChecksum>.Failure(Fail(client, error, "Get FTP checksum")); }
            finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
        }, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>Uses <c>APPE</c>. Not retried: a lost reply cannot tell whether the bytes were appended.</remarks>
    public async Task<Result<StorageItem>> AppendAsync(string path, Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var resolved = _paths.Resolve(path, requireNonRoot: true);
        if (resolved.IsFailure) return Result<StorageItem>.Failure(resolved.Error!);
        AsyncFtpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            // Forward-only, because FluentFTP rewinds seekable streams and would re-send bytes before a resume offset.
            var status = await client.UploadStream(new ForwardOnlyStream(source), resolved.Value!.RemotePath, FtpRemoteExists.AddToEnd, createRemoteDir: true, progress: null, cancellationToken).ConfigureAwait(false);
            if (status != FtpStatus.Success)
                return Result<StorageItem>.Failure(StorageErrors.ProviderError("The FTP server did not accept the append."));
            var item = await StatAsync(client, resolved.Value.RemotePath, cancellationToken).ConfigureAwait(false);
            return item is null
                ? Result<StorageItem>.Failure(StorageErrors.NotFound("The appended FTP file was not found."))
                : Result<StorageItem>.Success(ToItem(resolved.Value.StoragePath, item));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageItem>.Failure(Fail(client, error, "Append FTP file")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    /// <inheritdoc />
    public async Task<Result<StorageCommandResult>> ExecuteCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        if (!AllowRawCommands)
            return Result<StorageCommandResult>.Failure(StorageErrors.Unsupported("Raw commands are disabled for this connection (AllowRawCommands)."));
        if (string.IsNullOrWhiteSpace(command) || StoragePath.IndexOfControlCharacter(command) >= 0)
            return Result<StorageCommandResult>.Failure(StorageErrors.InvalidContent("A command must be one non-empty line without control characters."));
        AsyncFtpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            var reply = await client.Execute(command, cancellationToken).ConfigureAwait(false);
            var text = string.IsNullOrEmpty(reply.InfoMessages) ? reply.Message : $"{reply.InfoMessages}\n{reply.Message}";
            return Result<StorageCommandResult>.Success(new StorageCommandResult(reply.Success, reply.Code, text ?? string.Empty, string.Empty));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageCommandResult>.Failure(Fail(client, error, "Execute FTP command")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    /// <inheritdoc />
    /// <remarks>Uses <c>AVBL</c>, which reports available bytes on servers that implement it.</remarks>
    public async Task<Result<StorageSpaceInfo>> GetSpaceAsync(string path = "", CancellationToken cancellationToken = default)
    {
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure) return Result<StorageSpaceInfo>.Failure(resolved.Error!);
        if (RawArgumentError(resolved.Value!.RemotePath) is { } invalid) return Result<StorageSpaceInfo>.Failure(invalid);
        AsyncFtpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            var reply = await client.Execute($"AVBL {resolved.Value!.RemotePath}", cancellationToken).ConfigureAwait(false);
            return reply.Success && long.TryParse(reply.Message?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var available)
                ? Result<StorageSpaceInfo>.Success(new StorageSpaceInfo(null, available, null))
                : Result<StorageSpaceInfo>.Failure(StorageErrors.Unsupported("The FTP server does not report free space (AVBL)."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageSpaceInfo>.Failure(Fail(client, error, "Get FTP free space")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    /// <summary>
    /// Looks up one item. Servers without MLST answer through LIST, and many (vsftpd, for one) hide names
    /// starting with a dot from it, including the library's own staging files; those are found by listing the
    /// parent with hidden files included.
    /// </summary>
    private static async Task<FtpListItem?> FindAsync(AsyncFtpClient client, string remotePath, CancellationToken cancellationToken)
    {
        var name = remotePath.TrimEnd('/');
        var slash = name.LastIndexOf('/');
        var hidden = slash >= 0 && name[(slash + 1)..].StartsWith('.');
        // Without MLST a lookup lists the parent folder, and LIST hides dot names on many servers (vsftpd, for one):
        // a hidden name, such as the library's own staging files, is found by SIZE or a directory change instead.
        if (hidden && !client.HasFeature(FtpCapability.MLST) && client.HasFeature(FtpCapability.SIZE))
            return await StatAsync(client, remotePath, cancellationToken).ConfigureAwait(false);
        var item = await client.GetObjectInfo(remotePath, true, cancellationToken).ConfigureAwait(false);
        if (item is not null || !hidden || client.HasFeature(FtpCapability.MLST)) return item;
        var parent = slash == 0 ? "/" : name[..slash];
        FtpListItem[] listing;
        try
        {
            listing = await client.GetListing(parent, FtpListOption.Auto | FtpListOption.AllFiles, cancellationToken).ConfigureAwait(false);
        }
        catch (FluentFTP.Exceptions.FtpCommandException)
        {
            // A server that rejects "LIST -a" still lists what it shows.
            listing = await client.GetListing(parent, FtpListOption.Auto, cancellationToken).ConfigureAwait(false);
        }
        return listing.FirstOrDefault(entry => string.Equals(entry.FullName.TrimEnd('/'), name, StringComparison.Ordinal));
    }

    /// <summary>
    /// Looks up whether a path exists and whether it is a file or a directory without listing its folder: MLST
    /// where the server has it, otherwise SIZE (and MDTM) for a file and a directory change for a folder, which
    /// on a LIST-only server keeps a staged upload in a large folder from costing several full listings. A link
    /// is reported as what it points to; <see cref="FindAsync"/> gives the full listing entry.
    /// </summary>
    private static async Task<FtpListItem?> StatAsync(AsyncFtpClient client, string remotePath, CancellationToken cancellationToken)
    {
        if (client.HasFeature(FtpCapability.MLST) || !client.HasFeature(FtpCapability.SIZE))
            return await FindAsync(client, remotePath, cancellationToken).ConfigureAwait(false);
        var name = remotePath.TrimEnd('/');
        var leaf = name[(name.LastIndexOf('/') + 1)..];
        var size = await client.GetFileSize(name, -1, cancellationToken).ConfigureAwait(false);
        if (size >= 0)
        {
            var modified = client.HasFeature(FtpCapability.MDTM)
                ? await client.GetModifiedTime(name, cancellationToken).ConfigureAwait(false)
                : DateTime.MinValue;
            return new FtpListItem(leaf, size, FtpObjectType.File, modified) { FullName = name };
        }
        return await client.DirectoryExists(name, cancellationToken).ConfigureAwait(false)
            ? new FtpListItem(leaf, 0, FtpObjectType.Directory, DateTime.MinValue) { FullName = name }
            : null;
    }

    private StorageLinkInfo LinkInfo(string rawTarget, string linkParent)
    {
        var absolute = rawTarget.StartsWith('/') ? rawTarget : RemotePathResolver.Combine(linkParent, rawTarget);
        return new StorageLinkInfo(rawTarget, _paths.FromRemotePath(absolute));
    }

    private Task<Result> WithClientAsync(
        string path,
        string operation,
        RetryKind kind,
        Func<AsyncFtpClient, string, CancellationToken, Task> action,
        CancellationToken cancellationToken) =>
        _retry.ExecuteAsync(operation, kind, async (_, token) =>
        {
            var resolved = _paths.Resolve(path);
            if (resolved.IsFailure) return Result.Failure(resolved.Error!);
            AsyncFtpClient? client = null;
            try
            {
                client = await OpenClientAsync(token).ConfigureAwait(false);
                await action(client, resolved.Value!.RemotePath, token).ConfigureAwait(false);
                return Result.Success();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) { return Result.Failure(Fail(client, error, operation)); }
            finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
        }, cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        return _pool.ReleaseAsync();
    }

    /// <summary>
    /// Renames <paramref name="sourcePath"/> onto <paramref name="destinationPath"/>. An existing file destination
    /// (with <paramref name="overwrite"/>) is first renamed to a backup, restored if the rename fails, and removed
    /// after it succeeds; an existing directory is never replaced. A backup that cannot be removed after the
    /// commit is reported with <c>destinationState=complete</c> and <c>leftBehind</c>.
    /// </summary>
    private async Task<Result> CommitPathAsync(
        AsyncFtpClient client,
        string sourcePath,
        string destinationPath,
        FtpObjectType sourceType,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var destination = await StatAsync(client, destinationPath, cancellationToken).ConfigureAwait(false);
        if (destination is null)
        {
            var moved = await MovePathAsync(
                client,
                sourcePath,
                destinationPath,
                sourceType,
                cancellationToken).ConfigureAwait(false);
            return moved
                ? Result.Success()
                : Result.Failure(StorageErrors.ProviderError("The FTP server did not commit the item."));
        }
        if (!overwrite)
            return Result.Failure(StorageErrors.Conflict("The FTP destination already exists."));
        // Replacing a directory would delete everything in it (the backup is removed recursively): refuse, as a
        // local move does; the caller can merge through a copy instead.
        if (destination.Type == FtpObjectType.Directory)
            return Result.Failure(StorageErrors.Conflict("The FTP destination is an existing directory, which is not replaced."));

        var backup = await AllocateTemporaryPathAsync(
            client,
            RemotePathResolver.Parent(destinationPath),
            "backup",
            cancellationToken).ConfigureAwait(false);
        if (backup.IsFailure)
            return Result.Failure(backup.Error!);
        var backupPath = backup.Value!;
        if (!await MovePathAsync(
            client,
            destinationPath,
            backupPath,
            destination.Type,
            cancellationToken).ConfigureAwait(false))
            return Result.Failure(StorageErrors.ProviderError(
                "The FTP server could not stage the existing destination for replacement."));

        try
        {
            if (!await MovePathAsync(
                client,
                sourcePath,
                destinationPath,
                sourceType,
                cancellationToken).ConfigureAwait(false))
            {
                var restored = await TryRestoreBackupAsync(
                    client,
                    backupPath,
                    destinationPath,
                    destination.Type).ConfigureAwait(false);
                return restored
                    ? Result.Failure(StorageErrors.ProviderError("The FTP server did not commit the replacement."))
                    : Result.Failure(BackupKept(backupPath));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (await IsCommitCompleteAsync(client, sourcePath, destinationPath).ConfigureAwait(false))
                return await DeleteBackupAsync(client, backupPath).ConfigureAwait(false);
            _ = await TryRestoreBackupAsync(client, backupPath, destinationPath, destination.Type).ConfigureAwait(false);
            throw;
        }
        catch (Exception error)
        {
            if (await IsCommitCompleteAsync(client, sourcePath, destinationPath).ConfigureAwait(false))
                return await DeleteBackupAsync(client, backupPath).ConfigureAwait(false);
            var restored = await TryRestoreBackupAsync(
                client,
                backupPath,
                destinationPath,
                destination.Type).ConfigureAwait(false);
            return restored
                ? Result.Failure(Map(error, "Commit FTP replacement"))
                : Result.Failure(BackupKept(backupPath));
        }

        return await DeleteBackupAsync(client, backupPath).ConfigureAwait(false);
    }

    /// <summary>A replacement that failed and could not put the previous destination back: it is in the backup.</summary>
    private Error BackupKept(string backupPath) => StorageErrors.PartialFailure(
        "The FTP replacement failed and its previous destination could not be restored.",
        $"{StorageErrorInfo.DestinationStateKey}=backup;{StorageErrorInfo.LeftBehindKey}={_paths.FromRemotePath(backupPath) ?? backupPath}");

    private static Task<bool> MovePathAsync(
        AsyncFtpClient client,
        string sourcePath,
        string destinationPath,
        FtpObjectType sourceType,
        CancellationToken cancellationToken) =>
        sourceType == FtpObjectType.Directory
            ? client.MoveDirectory(sourcePath, destinationPath, FtpRemoteExists.Skip, cancellationToken)
            : client.MoveFile(sourcePath, destinationPath, FtpRemoteExists.Skip, cancellationToken);

    /// <summary>
    /// A new internal name in <paramref name="parent"/>. Random, so it is not probed: on servers that hide dot
    /// names from LIST a probe that misses costs a listing of the whole folder. Uploads and renames onto it refuse
    /// an existing name anyway.
    /// </summary>
    private static Task<Result<string>> AllocateTemporaryPathAsync(
        AsyncFtpClient client,
        string parent,
        string purpose,
        CancellationToken cancellationToken)
    {
        _ = client;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Result<string>.Success(parent.TrimEnd('/') + $"/.cl-storage-{purpose}-{Guid.NewGuid():N}.tmp"));
    }

    private static async Task<bool> IsCommitCompleteAsync(
        AsyncFtpClient client,
        string sourcePath,
        string destinationPath)
    {
        try
        {
            var source = await StatAsync(client, sourcePath, CancellationToken.None).ConfigureAwait(false);
            var destination = await StatAsync(client, destinationPath, CancellationToken.None).ConfigureAwait(false);
            return source is null && destination is not null;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> TryRestoreBackupAsync(
        AsyncFtpClient client,
        string backupPath,
        string destinationPath,
        FtpObjectType backupType)
    {
        try
        {
            if (await StatAsync(client, destinationPath, CancellationToken.None).ConfigureAwait(false) is not null)
                return false;
            return await MovePathAsync(
                client,
                backupPath,
                destinationPath,
                backupType,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Runs before the backup of a replaced file is removed, so a test can make the removal fail.</summary>
    internal Func<AsyncFtpClient, string, Task>? BeforeBackupDelete { get; init; }

    /// <summary>Removes the backup of a replaced file once the replacement committed.</summary>
    private async Task<Result> DeleteBackupAsync(AsyncFtpClient client, string backupPath)
    {
        try
        {
            if (BeforeBackupDelete is { } hook) await hook(client, backupPath).ConfigureAwait(false);
            await client.DeleteFile(backupPath, CancellationToken.None).ConfigureAwait(false);
            return Result.Success();
        }
        catch
        {
            return Result.Failure(StorageErrors.PartialFailure(
                "The FTP destination committed, but its temporary backup could not be removed.",
                $"{StorageErrorInfo.DestinationStateKey}=complete;backupState=present;{StorageErrorInfo.LeftBehindKey}={_paths.FromRemotePath(backupPath) ?? backupPath}"));
        }
    }

    private static async Task TryDeletePathAsync(AsyncFtpClient client, string path)
    {
        try
        {
            var item = await StatAsync(client, path, CancellationToken.None).ConfigureAwait(false);
            if (item is null)
                return;
            if (item.Type == FtpObjectType.Directory)
                await client.DeleteDirectory(path, FtpListOption.Recursive | FtpListOption.AllFiles, CancellationToken.None).ConfigureAwait(false);
            else
                await client.DeleteFile(path, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort cleanup must not mask the primary upload result.
        }
    }

    private Task<AsyncFtpClient> OpenClientAsync(CancellationToken cancellationToken) =>
        _clients.RentAsync(cancellationToken, _sessionOpened);

    /// <summary>Returns a client to the pool so the next operation reuses its control connection.</summary>
    private ValueTask ReleaseClientAsync(AsyncFtpClient client) => _clients.ReturnAsync(client);

    /// <summary>Sends NOOP so a session the server silently dropped is detected before reuse.</summary>
    private static async Task<bool> ProbeAsync(AsyncFtpClient client, CancellationToken cancellationToken)
    {
        var reply = await client.Execute("NOOP", cancellationToken).ConfigureAwait(false);
        return reply.Success && client.IsConnected;
    }

    private static async ValueTask DestroyClientAsync(AsyncFtpClient client)
    {
        try
        {
            if (client.IsConnected)
                await client.Disconnect(CancellationToken.None).ConfigureAwait(false);
        }
        catch { }
        finally { client.Dispose(); }
    }

    /// <summary>Maps a failure and retires the session when the failure left it unusable.</summary>
    private Error Fail(AsyncFtpClient? client, Exception exception, string operation)
    {
        var error = Map(exception, operation);
        if (client is not null && IsSessionFault(error))
            MarkFaulted(client, operation, error);
        return error;
    }

    /// <summary>Exception filter that retires a faulted session without catching the exception.</summary>
    private bool Observe(AsyncFtpClient? client, Exception exception, string operation)
    {
        if (client is not null && exception is not OperationCanceledException)
            _ = Fail(client, exception, operation);
        return false;
    }

    private void MarkFaulted(AsyncFtpClient client, string operation, Error? error = null)
    {
        _clients.MarkFaulted(client);
        try { _observer?.SessionFaulted(ConnectionId, StorageProvider.Ftp, operation, error ?? StorageErrors.ConnectionLost($"{operation}: the transfer was interrupted.")); }
        catch { /* Observers must never change the operation's outcome. */ }
    }

    /// <summary>Failures after which the session's protocol state is unknown and it must not be reused.</summary>
    private static bool IsSessionFault(Error error) =>
        StorageErrorInfo.IsConnectionFault(error) || error.Code == StorageErrors.TimeoutCode;

    private static StorageItem ToItem(string path, FtpListItem item)
    {
        var name = NameOf(path);
        return new StorageItem
        {
            Path = path,
            Name = name,
            ItemType = item.Type switch
            {
                FtpObjectType.Directory => StorageItemType.Directory,
                FtpObjectType.Link => StorageItemType.Link,
                _ => StorageItemType.File
            },
            Size = item.Type == FtpObjectType.File && item.Size >= 0 ? item.Size : null,
            LastModified = Utc(item.Modified),
            ModifiedPrecision = ListedPrecision(item),
            Created = Utc(item.Created),
            UnixMode = ModeOf(item.Chmod),
            Owner = string.IsNullOrWhiteSpace(item.RawOwner) ? null : item.RawOwner,
            Group = string.IsNullOrWhiteSpace(item.RawGroup) ? null : item.RawGroup,
            LinkTarget = string.IsNullOrWhiteSpace(item.LinkTarget) ? null : item.LinkTarget,
            IsHidden = name.StartsWith('.')
        };
    }

    /// <summary>
    /// How coarse a listed time is. A LIST line gives minutes ("Sep 24 12:34", "09-24-26 12:34PM"), or only a date
    /// for files older than about six months ("Sep 24 2025"); MLSD/MLST ("modify=") and MDTM, which leaves no line,
    /// give exact seconds. Comparing an exact time with a listed one must allow for this, or an unchanged file on a
    /// LIST-only server would look changed every time.
    /// </summary>
    internal static TimeSpan? ListedPrecision(FtpListItem item)
    {
        var line = item.Input;
        if (string.IsNullOrEmpty(line) || item.Modified == DateTime.MinValue) return null;
        if (line.Contains("modify=", StringComparison.OrdinalIgnoreCase)) return null;
        return ListedTimeOfDay.IsMatch(line) ? TimeSpan.FromMinutes(1) : TimeSpan.FromDays(1);
    }

    private static readonly System.Text.RegularExpressions.Regex ListedTimeOfDay = new(
        @"(?<![\d:])\d{1,2}:\d{2}(?![\d:])",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// The client converts listing times to UTC; an unspecified kind must not be reinterpreted as the
    /// local machine's time zone, which shifted times by the client's UTC offset.
    /// </summary>
    internal static DateTimeOffset? Utc(DateTime value) => value == DateTime.MinValue
        ? null
        : new DateTimeOffset(value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc));

    /// <summary>FluentFTP reports modes as decimal digits (755); converts them to permission bits.</summary>
    internal static int? ModeOf(int chmod) =>
        chmod > 0 && UnixPermissions.TryParseOctal(chmod.ToString(System.Globalization.CultureInfo.InvariantCulture), out var mode) ? mode : null;

    private static StorageItem DirectoryItem(string path) => new()
    {
        Path = path,
        Name = path.Length == 0 ? string.Empty : NameOf(path),
        ItemType = StorageItemType.Directory
    };

    private static StorageItem FileItem(string path, long? size) => new()
    {
        Path = path,
        Name = NameOf(path),
        ItemType = StorageItemType.File,
        Size = size,
        LastModified = DateTimeOffset.UtcNow
    };

    private static string NameOf(string path) => path.Split('/')[^1];

    internal static Error Map(Exception exception, string operation)
    {
        // FluentFTP wraps the server's reply in a generic FtpException ("see InnerException").
        if (exception is FtpException { InnerException: FtpCommandException or FtpInvalidCertificateException or IOException or System.Net.Sockets.SocketException } wrapped
            && exception is not FtpCommandException)
            return Map(wrapped.InnerException!, operation);
        switch (exception)
        {
            case FtpAuthenticationException auth:
                return StorageErrors.AuthenticationFailed($"{operation}: the FTP server rejected the credentials.", ReplyDetails(auth));
            case FtpCommandException command:
                return MapReply(command, operation);
            case FtpInvalidCertificateException:
                return StorageErrors.TlsFailure($"{operation}: the FTP server certificate was not trusted.",
                    $"{StorageErrorInfo.TlsReasonKey}={TlsDiagnosis.ServerCertificateRejected}");
            case FtpMissingObjectException:
                return StorageErrors.NotFound($"{operation}: item was not found.");
            case FtpProxyException:
                return StorageErrors.ConnectionFailed($"{operation}: could not connect through the proxy.");
        }
        if (ProviderErrorMapper.FromTransport(exception, operation, "FTP") is { } transport)
            return transport;
        if (exception is IOException or ObjectDisposedException)
            return StorageErrors.ConnectionLost($"{operation}: the FTP connection was lost.");
        return StorageErrors.ProviderError($"{operation}: FTP provider failed.", ProviderErrorMapper.ExceptionDetails(exception));
    }

    private static Error MapReply(FtpCommandException command, string operation)
    {
        var details = ReplyDetails(command);
        var message = command.Message ?? string.Empty;
        return command.CompletionCode switch
        {
            "421" when ContainsAny(message, "too many", "maximum", "max ", "limit", "busy", "try again")
                => StorageErrors.ServerBusy($"{operation}: the FTP server has too many sessions.", details),
            "421" => StorageErrors.ConnectionLost($"{operation}: the FTP server closed the connection.", details),
            "425" or "426" => StorageErrors.ConnectionLost($"{operation}: the FTP data connection failed.", details),
            "430" or "530" or "532" => StorageErrors.AuthenticationFailed($"{operation}: the FTP server rejected the credentials.", details),
            "434" => StorageErrors.ConnectionFailed($"{operation}: the FTP host is unavailable.", details),
            "450" or "550" when ContainsAny(message, "permission", "denied", "not allowed", "forbidden", "access")
                => StorageErrors.PermissionDenied($"{operation}: access was denied.", details),
            "450" or "550" when ContainsAny(message, "exists")
                => StorageErrors.Conflict($"{operation}: the item already exists.", details),
            "450" or "550" when ContainsAny(message, "not empty")
                => StorageErrors.Conflict($"{operation}: the directory is not empty.", details),
            "450" => StorageErrors.Unavailable($"{operation}: the FTP file is temporarily unavailable.", details),
            "550" => StorageErrors.NotFound($"{operation}: item was not found or is unavailable.", details),
            "452" or "552" => StorageErrors.QuotaExceeded($"{operation}: the FTP server has insufficient storage.", details),
            "553" when ContainsAny(message, "permission", "denied", "could not create", "cannot create", "read-only")
                => StorageErrors.PermissionDenied($"{operation}: the FTP server refused to create the file.", details),
            "501" or "553" => StorageErrors.InvalidPath($"{operation}: the FTP server rejected the path.", details),
            "502" or "504" => StorageErrors.Unsupported($"{operation}: the FTP server does not support this command.", details),
            "534" or "535" => StorageErrors.TlsFailure($"{operation}: the FTP server rejected the TLS request.", details),
            _ when command.CompletionCode is { Length: > 0 } code && code[0] == '4'
                => StorageErrors.Unavailable($"{operation}: the FTP server reported a transient failure.", details),
            _ => StorageErrors.ProviderError($"{operation}: FTP command failed.", details)
        };
    }

    private static string ReplyDetails(FtpCommandException command) =>
        $"{StorageErrorInfo.FtpReplyKey}={command.CompletionCode}";

    private static bool ContainsAny(string text, params string[] needles)
    {
        foreach (var needle in needles)
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
