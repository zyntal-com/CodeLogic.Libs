using System.Diagnostics.CodeAnalysis;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CL.Storage.Providers.Local;
using CodeLogic.Core.Results;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Messages.Transport;
using Renci.SshNet.Sftp;

namespace CL.Storage.Providers.Sftp;

public sealed partial class SftpStorageBackend
{

    internal ProviderPoolStats PoolStats => _clients.Stats;

    /// <summary>Creates the session pool for a client factory; shared pools are created once per settings.</summary>
    internal static ProviderClientPool<SftpClient> CreatePool(Func<SftpClient> clientFactory, StorageSessionConfig? session) =>
        new(
            clientFactory,
            SftpHostKeyTracker.ConnectAsync,
            static client => client.IsConnected,
            DestroyClientAsync,
            ProviderPoolOptions.From(session),
            ProbeAsync);

    /// <summary>Identifies the listing snapshots continuation tokens refer to; settings-based so tokens outlive a registration.</summary>
    internal string? ListingScope { get; init; }

    /// <summary>Records the host key each session is offered; set by the factory.</summary>
    internal ServerIdentityRecorder? Identity { get; init; }

    StorageServerIdentity? IStorageDiagnosticsSource.PresentedIdentity => Identity?.Last;

    StorageSessionPoolStats? IStorageDiagnosticsSource.PoolStats => StorageEndpoints.ToPublic(_clients.Stats);

    async Task<Result<StorageServerDetails>> IStorageDiagnosticsSource.GetServerDetailsAsync(CancellationToken cancellationToken)
    {
        SftpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            var info = client.ConnectionInfo;
            var negotiated = new Dictionary<string, string>(StringComparer.Ordinal);
            void Add(string key, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value)) negotiated[key] = value;
            }
            Add("kex", info.CurrentKeyExchangeAlgorithm);
            Add("hostKey", info.CurrentHostKeyAlgorithm);
            Add("cipher", info.CurrentServerEncryption);
            Add("mac", info.CurrentServerHmacAlgorithm);
            Add("compression", info.CurrentServerCompressionAlgorithm);
            Add("sftp", client.ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return Result<StorageServerDetails>.Success(new StorageServerDetails(
                string.IsNullOrWhiteSpace(info.ServerVersion) ? null : info.ServerVersion,
                SoftwareOf(info.ServerVersion),
                [],
                negotiated));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageServerDetails>.Failure(Fail(client, error, "Read SFTP server details")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    /// <summary>Reads the software part of an SSH version string, such as <c>OpenSSH_9.6</c> from <c>SSH-2.0-OpenSSH_9.6 Ubuntu</c>.</summary>
    internal static string? SoftwareOf(string? serverVersion)
    {
        if (string.IsNullOrWhiteSpace(serverVersion)) return null;
        var parts = serverVersion.Split('-', 3);
        if (parts.Length < 3) return null;
        var software = parts[2].Split(' ', 2)[0];
        return software.Length == 0 ? null : software;
    }

    /// <inheritdoc />
    public string ConnectionId { get; }
    /// <inheritdoc />
    public StorageProvider Provider => StorageProvider.Sftp;
    /// <inheritdoc />
    public string Root => _paths.Root;
    /// <inheritdoc />
    public StorageCapabilities Capabilities => CommandClientFactory is not null
        ? new StorageCapabilities(SftpCapabilities.Features | StorageFeature.RawCommands, SftpCapabilities.Limits)
        : SftpCapabilities;

    /// <summary>Creates SSH shell sessions for raw commands; set only when the connection enables <c>AllowRawCommands</c>.</summary>
    internal Func<SshClient>? CommandClientFactory { get; init; }

    /// <inheritdoc />
    public Task<Result<StorageItem>> GetInfoAsync(string path, CancellationToken cancellationToken = default) =>
        _retry.ExecuteAsync("Get SFTP item info", RetryKind.Idempotent, (_, token) => GetInfoCoreAsync(path, token), cancellationToken);

    private async Task<Result<StorageItem>> GetInfoCoreAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure) return Result<StorageItem>.Failure(resolved.Error!);
        if (resolved.Value!.StoragePath.Length == 0)
            return Result<StorageItem>.Success(DirectoryItem(string.Empty));
        SftpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            if (!await client.ExistsAsync(resolved.Value.RemotePath, cancellationToken).ConfigureAwait(false))
                return Result<StorageItem>.Failure(StorageErrors.NotFound($"SFTP item '{resolved.Value.StoragePath}' was not found."));
            var attributes = await client.GetAttributesAsync(resolved.Value.RemotePath, cancellationToken).ConfigureAwait(false);
            return Result<StorageItem>.Success(ToItem(resolved.Value.StoragePath, attributes));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageItem>.Failure(Fail(client, error, "Get SFTP item info")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    /// <inheritdoc />
    public Task<Result<bool>> ExistsAsync(string path, CancellationToken cancellationToken = default) =>
        _retry.ExecuteAsync("Check SFTP item existence", RetryKind.Idempotent, (_, token) => ExistsCoreAsync(path, token), cancellationToken);

    private async Task<Result<bool>> ExistsCoreAsync(string path, CancellationToken cancellationToken)
    {
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure) return Result<bool>.Failure(resolved.Error!);
        SftpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            return Result<bool>.Success(await client.ExistsAsync(resolved.Value!.RemotePath, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<bool>.Failure(Fail(client, error, "Check SFTP item existence")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    /// <inheritdoc />
    public Task<Result<StoragePage>> ListAsync(string path, StorageListOptions? options = null, CancellationToken cancellationToken = default) =>
        _retry.ExecuteAsync("List SFTP directory", RetryKind.Idempotent, (_, token) => ListCoreAsync(path, options, token), cancellationToken);

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
            // The walk runs only when no snapshot is cached for this listing pass, so pages after
            // the first cost neither an SSH handshake nor a directory traversal.
            return await ProviderPaging.CreateAsync(
                ProviderPaging.Scope(ListingScope ?? ConnectionId, target.StoragePath, options.Recursive),
                options,
                async token =>
                {
                    SftpClient? listClient = null;
                    try
                    {
                        listClient = await OpenClientAsync(token).ConfigureAwait(false);
                        if (!await listClient.ExistsAsync(target.RemotePath, token).ConfigureAwait(false))
                            return Result<IEnumerable<StorageItem>>.Failure(
                                StorageErrors.NotFound($"SFTP directory '{target.StoragePath}' was not found."));
                        var rootAttributes = await listClient.GetAttributesAsync(target.RemotePath, token).ConfigureAwait(false);
                        if (!rootAttributes.IsDirectory)
                            return Result<IEnumerable<StorageItem>>.Failure(
                                StorageErrors.Conflict("The SFTP listing path is not a directory."));
                        var items = await CollectListingAsync(listClient, target.RemotePath, options.Recursive, token).ConfigureAwait(false);
                        return Result<IEnumerable<StorageItem>>.Success(items);
                    }
                    catch (Exception error) when (Observe(listClient, error, "List SFTP directory")) { throw; }
                    finally { if (listClient is not null) await ReleaseClientAsync(listClient).ConfigureAwait(false); }
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StoragePage>.Failure(Map(error, "List SFTP directory")); }
    }

    /// <inheritdoc />
    public Task<Result> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
        _retry.ExecuteAsync("Create SFTP directory", RetryKind.Idempotent, (_, token) => CreateDirectoryCoreAsync(path, token), cancellationToken);

    private async Task<Result> CreateDirectoryCoreAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure) return Result.Failure(resolved.Error!);
        SftpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            await EnsureDirectoryAsync(client, resolved.Value!.RemotePath, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Fail(client, error, "Create SFTP directory")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    /// <inheritdoc />
    /// <remarks>Uploads are staged and renamed into place, so replaying a seekable source cannot leave a partial file.</remarks>
    public Task<Result<StorageItem>> UploadAsync(string path, Stream source, StorageUploadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (StorageTransferPipeline.Applies(this, options))
            return StorageTransferPipeline.UploadAsync(this, path, source, options, cancellationToken);
        return _retry.ExecuteUploadAsync("Upload SFTP file", source, token => UploadCoreAsync(path, source, options, token), cancellationToken);
    }

    private async Task<Result<StorageItem>> UploadCoreAsync(string path, Stream source, StorageUploadOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new StorageUploadOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<StorageItem>.Failure(validation.Error!);
        if (options.Condition is { IsEmpty: false })
            return Result<StorageItem>.Failure(StorageErrors.Unsupported(
                "SFTP does not expose portable atomic upload conditions."));
        if (options.Metadata.Count > 0)
            return Result<StorageItem>.Failure(StorageErrors.Unsupported(
                "SFTP does not support portable user metadata."));
        var resolved = _paths.Resolve(path, requireNonRoot: true);
        if (resolved.IsFailure) return Result<StorageItem>.Failure(resolved.Error!);
        SftpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            return await UploadWithClientAsync(client, resolved.Value!, source, options.Overwrite, options.CreateParents, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageItem>.Failure(Fail(client, error, "Upload SFTP file")); }
        finally
        {
            if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false);
        }
    }
}
