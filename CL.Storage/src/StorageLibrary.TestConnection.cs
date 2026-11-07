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

    /// <summary>
    /// Tests a connection configuration without saving or registering it: validates it, connects and
    /// authenticates, lists the root, and reads server details. Works before the library is initialized.
    /// </summary>
    /// <remarks>
    /// When the server's certificate or host key is rejected, <see cref="StorageConnectionTestReport.ServerIdentity"/>
    /// still carries what it presented, so a setup screen can ask "the server presented this fingerprint; trust it?".
    /// </remarks>
    /// <param name="connection">Provider connection settings, such as <see cref="SftpConnectionConfig"/>.</param>
    /// <param name="cancellationToken">Token used to cancel the test.</param>
    /// <returns>The steps that ran and what they found.</returns>
    public Task<StorageConnectionTestReport> TestConnectionAsync(
        StorageConnectionConfigBase connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var (host, port, security) = StorageEndpoints.Describe(connection);
        return TestConnectionCoreAsync(
            connection.GetValidationErrors().ToArray(),
            connection.GetType(),
            // A pool of its own: a test must never use or disturb the sessions of a live connection with the same settings.
            () => _factories[connection.GetType()] is IIsolatedStorageBackendFactory isolated
                ? isolated.CreateIsolated("connection-test", CloneProviderConnection(connection), DefaultTestBufferBytes)
                : _factories[connection.GetType()].Create("connection-test", CloneProviderConnection(connection), DefaultTestBufferBytes),
            host,
            port,
            security,
            cancellationToken);
    }

    /// <summary>Tests local folder settings without saving or registering them.</summary>
    /// <param name="connection">Local folder settings.</param>
    /// <param name="cancellationToken">Token used to cancel the test.</param>
    /// <returns>The steps that ran and what they found.</returns>
    public Task<StorageConnectionTestReport> TestConnectionAsync(
        LocalConnectionConfig connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return TestConnectionCoreAsync(
            [.. connection.Validate().Errors],
            typeof(LocalConnectionConfig),
            () => _factories[typeof(LocalConnectionConfig)].Create("connection-test", connection, DefaultTestBufferBytes),
            null,
            null,
            StorageTransportSecurity.Unknown,
            cancellationToken);
    }

    private async Task<StorageConnectionTestReport> TestConnectionCoreAsync(
        string[] validationErrors,
        Type configurationType,
        Func<IStorageBackend> create,
        string? host,
        int? port,
        StorageTransportSecurity security,
        CancellationToken cancellationToken)
    {
        var steps = new List<StorageConnectionTestStep>();
        StorageConnectionTestReport Fail(Error error, IStorageBackend? backend) => new(
            false, steps, null, (backend as IStorageDiagnosticsSource)?.PresentedIdentity, error);

        var started = Stopwatch.GetTimestamp();
        if (validationErrors.Length > 0 || !_factories.ContainsKey(configurationType))
        {
            var invalid = validationErrors.Length > 0
                ? StorageErrors.InvalidContent($"The connection settings are invalid: {string.Join("; ", validationErrors)}")
                : StorageErrors.Unsupported($"Connection configuration type '{configurationType.Name}' does not have a provider factory.");
            steps.Add(new StorageConnectionTestStep("validate", false, Stopwatch.GetElapsedTime(started), invalid));
            return Fail(invalid, null);
        }

        IStorageBackend backend;
        try
        {
            backend = create();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The exception type only: its message can carry hosts, paths, or credentials.
            var invalid = StorageErrors.InvalidContent($"The connection settings could not be applied ({error.GetType().Name}).");
            steps.Add(new StorageConnectionTestStep("validate", false, Stopwatch.GetElapsedTime(started), invalid));
            return Fail(invalid, null);
        }
        steps.Add(new StorageConnectionTestStep("validate", true, Stopwatch.GetElapsedTime(started), null));

        await using (backend.ConfigureAwait(false))
        {
            async Task<Result<T>> Step<T>(string name, Func<Task<Result<T>>> run)
            {
                var stepStarted = Stopwatch.GetTimestamp();
                Result<T> result;
                try
                {
                    result = await run().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    result = Result<T>.Failure(ProviderErrorMapper.FromTransport(error, $"Connection test '{name}'", "server")
                        ?? StorageErrors.FromException(error, $"Connection test step '{name}'"));
                }
                steps.Add(new StorageConnectionTestStep(name, result.IsSuccess, Stopwatch.GetElapsedTime(stepStarted), result.Error));
                return result;
            }

            var connected = await Step("connect", async () =>
            {
                var health = await backend.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
                return health.IsSuccess ? Result<bool>.Success(true) : Result<bool>.Failure(health.Error!);
            }).ConfigureAwait(false);
            if (connected.IsFailure) return Fail(connected.Error!, backend);

            var listed = await Step("list", () => backend.ListAsync(string.Empty, new StorageListOptions { PageSize = 1 }, cancellationToken))
                .ConfigureAwait(false);
            if (listed.IsFailure) return Fail(listed.Error!, backend);

            var described = await Step("details", () => DiagnoseAsync(backend, host, port, security, null, cancellationToken))
                .ConfigureAwait(false);
            // Server details are informational: a server that refuses OPTIONS is still usable.
            var source = backend as IStorageDiagnosticsSource;
            return new StorageConnectionTestReport(
                true,
                steps,
                described.IsSuccess ? described.Value : null,
                source?.PresentedIdentity,
                null);
        }
    }

    /// <summary>
    /// Copies a file or directory between mounted connections. Cross-connection transfers use a
    /// bounded streaming relay and a unique destination staging object before final commit.
    /// </summary>
    /// <returns>
    /// What happened: committed or skipped (and why), the digest and new destination identity, and, when it
    /// did not finish, exactly what state it left and a token to resume it.
    /// </returns>
    public Task<StorageTransferReport> CopyAsync(
        string sourceConnectionId,
        string sourcePath,
        string destinationConnectionId,
        string destinationPath,
        StorageTransferOptions? options = null,
        CancellationToken cancellationToken = default) =>
        TransferAsync(
            sourceConnectionId,
            sourcePath,
            destinationConnectionId,
            destinationPath,
            options,
            move: false,
            cancellationToken);

    /// <summary>
    /// Uploads a complete local directory through the bounded, rollback-safe transfer coordinator.
    /// Local links/reparse points are rejected rather than followed.
    /// </summary>
    public async Task<Result<StorageDirectoryTransferReport>> UploadDirectoryAsync(
        string sourceDirectoryPath,
        string destinationConnectionId,
        string destinationPath,
        StorageTransferOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionId(destinationConnectionId);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new StorageTransferOptions();
        var validation = options.Validate();
        if (validation.IsFailure)
            return Result<StorageDirectoryTransferReport>.Failure(validation.Error!);
        if (string.IsNullOrWhiteSpace(sourceDirectoryPath))
            return Result<StorageDirectoryTransferReport>.Failure(StorageErrors.InvalidPath(
                "A local source directory path is required."));
        var normalizedDestination = StoragePath.Normalize(destinationPath);
        if (normalizedDestination.IsFailure)
            return Result<StorageDirectoryTransferReport>.Failure(normalizedDestination.Error!);

        string localRoot;
        try
        {
            localRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectoryPath));
            if (!Directory.Exists(localRoot))
                return Result<StorageDirectoryTransferReport>.Failure(StorageErrors.NotFound(
                    "The local upload source directory was not found."));
        }
        catch (Exception error)
        {
            return Result<StorageDirectoryTransferReport>.Failure(StorageErrors.FromException(
                error,
                "Resolve local upload directory"));
        }

        var source = new LocalStorageBackend(
            ".cl-storage-directory-upload",
            new LocalConnectionConfig { RootPath = localRoot, FollowLinks = false },
            _storageConfig?.MaxBufferedDownloadBytes ?? LocalStorageBackend.DefaultMaxBufferedDownloadBytes);
        await using var sourceLifetime = source.ConfigureAwait(false);
        BackendEntry.BackendOperationLease? destinationLease = null;
        StorageEventPublisher? publisher = null;
        StorageDirectoryTransferReport? report = null;
        string? destinationId = null;
        StorageProvider destinationProvider = default;
        try
        {
            destinationLease = AcquireOperation(destinationConnectionId);
            destinationId = destinationLease.Backend.ConnectionId;
            destinationProvider = destinationLease.Backend.Provider;
            var copied = await StorageTransferCoordinator.CopyAsync(
                source,
                string.Empty,
                destinationLease.Backend,
                normalizedDestination.Value!,
                options,
                cancellationToken).ConfigureAwait(false);
            if (copied.IsFailure)
                return Result<StorageDirectoryTransferReport>.Failure(copied.Error!);
            report = new StorageDirectoryTransferReport(
                copied.Value!.Files,
                copied.Value.Directories,
                copied.Value.Bytes,
                copied.Value.SkippedFiles);
            publisher = CaptureEventPublisher();
        }
        finally
        {
            destinationLease?.Dispose();
        }

        await publisher!.PublishAsync(new StorageDirectoryUploadedEvent(
            destinationId!,
            destinationProvider,
            normalizedDestination.Value!,
            report!.Files,
            report.Directories,
            report.Bytes,
            DateTimeOffset.UtcNow)).ConfigureAwait(false);
        return Result<StorageDirectoryTransferReport>.Success(report);
    }
}
