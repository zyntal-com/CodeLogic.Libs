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

/// <summary>Provider-neutral storage library for CodeLogic applications.</summary>
public sealed partial class StorageLibrary : ILibrary, IAsyncDisposable
{
    private readonly object _stateGate = new();
    private readonly object _registryGate = new();
    // Transfer queues opened from this library; stopping the library stops them first (B43).
    private readonly List<Queue.StorageTransferQueue> _queues = [];
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly Dictionary<string, BackendEntry> _registry = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, StorageServiceProxy> _proxies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, StorageConnectionInfo> _connectionInfos = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<Type, IStorageBackendFactory> _factories;
    private readonly Action? _defaultConnectionSnapshotCaptured;
    private readonly StorageConnectionObserver _connectionObserver;
    private readonly bool _runtimeOnly;
    private readonly StorageConfig? _runtimeSettings;
    private readonly ConcurrentDictionary<Type, object> _memoryConfig = new();
    private readonly ConcurrentDictionary<string, StorageConnectionHealth> _health = new(StringComparer.OrdinalIgnoreCase);
    private LibraryContext? _context;
    private StorageConfig? _storageConfig;
    private TransferLimits _libraryLimits = TransferLimits.None;
    private LocalStorageConfig? _localConfig;
    private LocalStorageConfig? _persistedLocalConfig;
    private readonly Dictionary<string, LocalConnectionConfig?> _runtimeLocalOverrides = new(StringComparer.OrdinalIgnoreCase);
    private Task? _stopTask;
    private LifecycleState _state;
    private bool _enabled;

    /// <summary>Initializes the storage library with every built-in provider factory.</summary>
    public StorageLibrary() : this(new StorageLibraryOptions())
    {
    }

    /// <summary>Initializes the storage library with every built-in provider factory and the given options.</summary>
    /// <param name="options">Library options, such as <see cref="StorageLibraryOptions.RuntimeOnly"/>.</param>
    public StorageLibrary(StorageLibraryOptions options) : this([
        new LocalStorageBackendFactory(),
        new S3StorageBackendFactory(),
        new FtpStorageBackendFactory(),
        new SftpStorageBackendFactory(),
        new WebDavStorageBackendFactory(),
        new AzureBlobStorageBackendFactory(),
        new GoogleCloudStorageBackendFactory(),
        new SwiftStorageBackendFactory()
    ], null, options)
    { }

    internal StorageLibrary(IEnumerable<IStorageBackendFactory> factories) : this(factories, null) { }

    internal StorageLibrary(
        IEnumerable<IStorageBackendFactory> factories,
        Action? defaultConnectionSnapshotCaptured,
        StorageLibraryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(factories);
        _runtimeOnly = options?.RuntimeOnly ?? false;
        // A copy: the caller changing its settings object later must not change the library's.
        _runtimeSettings = options?.Settings is { } settings ? CloneStorageConfig(settings) : null;
        _factories = factories.ToDictionary(factory => factory.ConfigurationType);
        _defaultConnectionSnapshotCaptured = defaultConnectionSnapshotCaptured;
        _connectionObserver = new StorageConnectionObserver(TryCaptureEventPublisher, TryCaptureLogger);
        if (_factories.Count == 0)
            throw new ArgumentException("At least one storage backend factory is required.", nameof(factories));
    }

    /// <inheritdoc />
    public LibraryManifest Manifest { get; } = new()
    {
        Id = "CL.Storage",
        Name = "Storage Library",
        Version = CL.Internal.InternalLibraryVersion.Current,
        Description = "Provider-neutral mounted storage connections",
        Author = "Zyntal.com",
        Tags = ["storage", "filesystem", "cloud"]
    };

    /// <summary>Returns the service for the configured default connection.</summary>
    public IStorageService DefaultStorage => GetStorage(GetDefaultConnectionId());

    /// <inheritdoc />
    public Task OnConfigureAsync(LibraryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_stateGate)
        {
            if (_state != LifecycleState.Created)
                throw new InvalidOperationException("Storage library configuration can only run once.");
            _context = context;
            _state = LifecycleState.Configured;
        }

        // Runtime-only libraries never touch configuration files; sections live in memory instead.
        if (_runtimeOnly)
            return Task.CompletedTask;
        context.Configuration.Register<StorageConfig>("storage");
        context.Configuration.Register<LocalStorageConfig>("storage.local");
        context.Configuration.Register<S3StorageConfig>("storage.s3");
        context.Configuration.Register<FtpStorageConfig>("storage.ftp");
        context.Configuration.Register<SftpStorageConfig>("storage.sftp");
        context.Configuration.Register<WebDavStorageConfig>("storage.webdav");
        context.Configuration.Register<AzureStorageConfig>("storage.azure");
        context.Configuration.Register<GoogleCloudStorageConfig>("storage.gcs");
        context.Configuration.Register<SwiftStorageConfig>("storage.swift");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task OnInitializeAsync(LibraryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_stateGate)
        {
            if (_state != LifecycleState.Configured)
                throw new InvalidOperationException("Storage library must be configured before it is initialized.");
            if (!ReferenceEquals(_context, context))
                throw new InvalidOperationException("Storage library lifecycle phases must use the same LibraryContext.");
        }

        var storage = ReadConfig<StorageConfig>(context);
        var local = ReadConfig<LocalStorageConfig>(context);
        var s3 = ReadConfig<S3StorageConfig>(context);
        var ftp = ReadConfig<FtpStorageConfig>(context);
        var sftp = ReadConfig<SftpStorageConfig>(context);
        var webDav = ReadConfig<WebDavStorageConfig>(context);
        var azure = ReadConfig<AzureStorageConfig>(context);
        var gcs = ReadConfig<GoogleCloudStorageConfig>(context);
        var swift = ReadConfig<SwiftStorageConfig>(context);

        EnsureValid("storage", storage.Validate());
        EnsureValid("storage.local", local.Validate());
        EnsureValid("storage.s3", s3.Validate());
        EnsureValid("storage.ftp", ftp.Validate());
        EnsureValid("storage.sftp", sftp.Validate());
        EnsureValid("storage.webdav", webDav.Validate());
        EnsureValid("storage.azure", azure.Validate());
        EnsureValid("storage.gcs", gcs.Validate());
        EnsureValid("storage.swift", swift.Validate());
        var libraryLimits = StorageTransferPipeline.LibraryLimits(storage.MaxTotalUploadBytesPerSecond, storage.MaxTotalDownloadBytesPerSecond);

        ValidateGlobalIds(local, s3, ftp, sftp, webDav, azure, gcs, swift);

        var builtEntries = new Dictionary<string, BackendEntry>(StringComparer.OrdinalIgnoreCase);
        var infos = new Dictionary<string, StorageConnectionInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (storage.Enabled)
            {
                foreach (var (id, configuration) in local.Connections)
                {
                    infos.Add(id, new StorageConnectionInfo(id, StorageProvider.Local, configuration.RootPath, configuration.Enabled));
                    if (!configuration.Enabled)
                        continue;
                    var backend = WithLimits(_factories[typeof(LocalConnectionConfig)].Create(
                        id,
                        configuration,
                        storage.MaxBufferedDownloadBytes), configuration, libraryLimits);
                    builtEntries.Add(id, new BackendEntry(backend, ownsBackend: true));
                }

                AddProviderConnections(builtEntries, infos, s3, StorageProvider.S3, storage.MaxBufferedDownloadBytes, libraryLimits);
                AddProviderConnections(builtEntries, infos, ftp, StorageProvider.Ftp, storage.MaxBufferedDownloadBytes, libraryLimits);
                AddProviderConnections(builtEntries, infos, sftp, StorageProvider.Sftp, storage.MaxBufferedDownloadBytes, libraryLimits);
                AddProviderConnections(builtEntries, infos, webDav, StorageProvider.WebDav, storage.MaxBufferedDownloadBytes, libraryLimits);
                AddProviderConnections(builtEntries, infos, azure, StorageProvider.AzureBlob, storage.MaxBufferedDownloadBytes, libraryLimits);
                AddProviderConnections(builtEntries, infos, gcs, StorageProvider.GoogleCloudStorage, storage.MaxBufferedDownloadBytes, libraryLimits);
                AddProviderConnections(builtEntries, infos, swift, StorageProvider.OpenStackSwift, storage.MaxBufferedDownloadBytes, libraryLimits);

                if (!_runtimeOnly && !builtEntries.ContainsKey(storage.DefaultConnection))
                    throw new InvalidOperationException(
                        $"The configured default storage connection '{storage.DefaultConnection}' is not available from an enabled provider factory.");
            }

            lock (_stateGate)
            {
                if (_state != LifecycleState.Configured || !ReferenceEquals(_context, context))
                    throw new InvalidOperationException("Storage library stopped before initialization completed.");

                lock (_registryGate)
                {
                    foreach (var pair in builtEntries)
                        _registry.Add(pair.Key, pair.Value);
                    foreach (var pair in infos)
                        _connectionInfos.Add(pair.Key, pair.Value);
                }

                _storageConfig = storage;
                _libraryLimits = libraryLimits;
                _localConfig = local;
                _persistedLocalConfig = CloneLocalConfig(local);
                _runtimeLocalOverrides.Clear();
                _enabled = storage.Enabled;
                _state = LifecycleState.Initialized;
            }
        }
        catch
        {
            await Task.WhenAll(builtEntries.Values.Select(entry => entry.RetireAndDisposeAsync())).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public Task OnStartAsync(LibraryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_stateGate)
        {
            if (_state != LifecycleState.Initialized)
                throw new InvalidOperationException("Storage library must be initialized before it is started.");
            if (!ReferenceEquals(_context, context))
                throw new InvalidOperationException("Storage library lifecycle phases must use the same LibraryContext.");
            _state = LifecycleState.Started;
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnStopAsync()
    {
        lock (_stateGate)
        {
            if (_state is LifecycleState.Stopped or LifecycleState.Disposed)
                return Task.CompletedTask;
            if (_stopTask is not null)
                return _stopTask;
            _state = LifecycleState.Stopping;
            _stopTask = StopCoreAsync();
            return _stopTask;
        }
    }

    private async Task StopCoreAsync()
    {
        BackendEntry[] entries = [];
        var gateEntered = false;
        try
        {
            // Queues first, while their connections still work: running jobs record where they stopped
            // (queued again or interrupted) instead of failing on retired connections.
            Queue.StorageTransferQueue[] queues;
            lock (_queues) queues = [.. _queues];
            await Task.WhenAll(queues.Select(queue => queue.DisposeAsync().AsTask())).ConfigureAwait(false);

            await _mutationGate.WaitAsync().ConfigureAwait(false);
            gateEntered = true;
            try
            {
                lock (_registryGate)
                {
                    entries = _registry.Values.ToArray();
                    _registry.Clear();
                    _connectionInfos.Clear();
                }
            }
            finally
            {
                _mutationGate.Release();
                gateEntered = false;
            }

            var disposals = entries
                .Select(entry => (entry.Backend.ConnectionId, Task: entry.RetireAndDisposeAsync()))
                .ToArray();
            try
            {
                await Task.WhenAll(disposals.Select(disposal => disposal.Task)).ConfigureAwait(false);
            }
            catch
            {
                var failedIds = new List<string>();
                foreach (var disposal in disposals.Where(disposal => disposal.Task.IsFaulted))
                {
                    failedIds.Add(disposal.ConnectionId);
                    _context?.Logger.Error(
                        $"Storage backend '{disposal.ConnectionId}' failed during disposal.",
                        disposal.Task.Exception?.GetBaseException());
                }
                throw new InvalidOperationException(
                    $"Failed to dispose storage backend(s): {string.Join(", ", failedIds)}.");
            }
            // Sessions kept warm for re-registration (LingerSeconds) do not outlive the library; another
            // library's are left alone. Its connections were created with this observer, which identifies it.
            await Providers.SharedResources.FlushIdleAsync(_connectionObserver).ConfigureAwait(false);
        }
        finally
        {
            if (gateEntered)
                _mutationGate.Release();
            lock (_stateGate)
            {
                _context = null;
                _storageConfig = null;
                _localConfig = null;
                _persistedLocalConfig = null;
                _runtimeLocalOverrides.Clear();
                _enabled = false;
                _state = LifecycleState.Stopped;
            }
        }
    }

    /// <inheritdoc />
    public async Task<HealthStatus> HealthCheckAsync()
    {
        if (!TryCaptureRuntimeSnapshot(out _, out var state))
            return HealthStatus.Unhealthy($"Storage library is {DescribeState(state)}");

        var targets = new List<HealthTarget>();
        int timeoutSeconds;
        ILogger logger;
        try
        {
            await _mutationGate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return HealthStatus.Unhealthy("Storage library is disposed");
        }

        try
        {
            if (!TryCaptureRuntimeSnapshot(out var runtime, out state))
                return HealthStatus.Unhealthy($"Storage library is {DescribeState(state)}");
            if (!runtime.Enabled)
                return HealthStatus.Healthy("Storage library is disabled");

            timeoutSeconds = runtime.HealthCheckTimeoutSeconds;
            logger = runtime.Logger;

            lock (_registryGate)
            {
                if (!_runtimeOnly && !_registry.ContainsKey(runtime.DefaultConnection))
                    return HealthStatus.Unhealthy($"Default storage connection '{runtime.DefaultConnection}' is unavailable");
                foreach (var (id, entry) in _registry)
                {
                    if (!entry.TryAcquire(out var lease))
                    {
                        foreach (var target in targets)
                            target.Lease.Dispose();
                        targets.Clear();
                        return HealthStatus.Unhealthy($"Storage connection '{id}' is retiring");
                    }
                    targets.Add(new HealthTarget(id, lease!));
                }
            }
        }
        finally
        {
            _mutationGate.Release();
        }

        var probes = await Task.WhenAll(
            targets.Select(target => ProbeHealthAsync(target, timeoutSeconds, logger))).ConfigureAwait(false);
        foreach (var probe in probes)
            RecordHealth(probe.Id, probe.Provider, probe.Healthy, probe.Healthy ? null : probe.Detail, probe.Latency);

        state = GetLifecycleState();
        if (state is not (LifecycleState.Initialized or LifecycleState.Started))
            return HealthStatus.Unhealthy($"Storage library is {DescribeState(state)}");

        var failed = probes.Where(probe => !probe.Healthy).ToArray();
        if (failed.Length == 0)
            return HealthStatus.Healthy($"All {probes.Length} storage connection(s) are healthy");

        var failedIds = string.Join(", ", failed.Select(probe => probe.Id));
        return new HealthStatus
        {
            Status = failed.Length == probes.Length ? HealthStatusLevel.Unhealthy : HealthStatusLevel.Degraded,
            Message = failed.Length == probes.Length
                ? $"All storage connections failed: {failedIds}"
                : $"Storage connections unavailable: {failedIds}",
            Data = new Dictionary<string, object>
            {
                ["failedConnections"] = failed.ToDictionary(
                    probe => probe.Id,
                    probe => (object)probe.Detail,
                    StringComparer.OrdinalIgnoreCase)
            }
        };
    }

    /// <summary>Returns a stable service proxy for a named effective connection.</summary>
    public IStorageService GetStorage(string connectionId = "Default")
    {
        ValidateConnectionId(connectionId);
        EnsureOperational();
        lock (_registryGate)
        {
            if (!_registry.TryGetValue(connectionId, out var entry))
                throw new KeyNotFoundException($"Storage connection '{connectionId}' is not registered or enabled.");
            var effectiveId = entry.Backend.ConnectionId;
            if (!_proxies.TryGetValue(effectiveId, out var proxy))
            {
                proxy = new StorageServiceProxy(this, effectiveId);
                _proxies.Add(effectiveId, proxy);
            }
            return proxy;
        }
    }

    /// <summary>Attempts to return a stable service proxy without throwing for an unknown or disabled connection.</summary>
    public bool TryGetStorage(
        string connectionId,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IStorageService? storage)
    {
        ValidateConnectionId(connectionId);
        EnsureOperational();
        lock (_registryGate)
        {
            if (!_registry.TryGetValue(connectionId, out var entry))
            {
                storage = null;
                return false;
            }
            var effectiveId = entry.Backend.ConnectionId;
            if (!_proxies.TryGetValue(effectiveId, out var proxy))
            {
                proxy = new StorageServiceProxy(this, effectiveId);
                _proxies.Add(effectiveId, proxy);
            }
            storage = proxy;
            return true;
        }
    }

    /// <summary>Checks one connection using the configured health timeout.</summary>
    public async Task<Result<HealthStatus>> CheckConnectionHealthAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionId(connectionId);
        cancellationToken.ThrowIfCancellationRequested();
        var runtime = CaptureRuntimeSnapshot();
        using var lease = AcquireOperation(connectionId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(runtime.HealthCheckTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var started = Stopwatch.GetTimestamp();
        var result = await CheckConnectionHealthCoreAsync(lease.Backend, linked.Token, timeout, cancellationToken).ConfigureAwait(false);
        RecordHealth(lease.Backend.ConnectionId, lease.Backend.Provider, result.IsSuccess, result.Error?.Code, Stopwatch.GetElapsedTime(started));
        return result;
    }

    private static async Task<Result<HealthStatus>> CheckConnectionHealthCoreAsync(
        IStorageBackend backend,
        CancellationToken probeToken,
        CancellationTokenSource timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            var health = await backend.CheckHealthAsync(probeToken).ConfigureAwait(false);
            if (health.IsFailure)
                return Result<HealthStatus>.Failure(health.Error!);
            return Result<HealthStatus>.Success(HealthStatus.Healthy(
                $"Storage connection '{backend.ConnectionId}' is healthy"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return Result<HealthStatus>.Failure(StorageErrors.Timeout(
                $"Storage connection '{backend.ConnectionId}' timed out during health check."));
        }
        catch (TimeoutException)
        {
            return Result<HealthStatus>.Failure(StorageErrors.Timeout(
                $"Storage connection '{backend.ConnectionId}' timed out during health check."));
        }
        catch (Exception)
        {
            return Result<HealthStatus>.Failure(StorageErrors.ProviderError(
                $"Storage connection '{backend.ConnectionId}' failed its health check."));
        }
    }

    /// <summary>Stores a health result and publishes <see cref="StorageConnectionHealthChangedEvent"/> when the state flips.</summary>
    private void RecordHealth(string id, StorageProvider provider, bool healthy, string? errorCode, TimeSpan latency)
    {
        var current = new StorageConnectionHealth(healthy, healthy ? null : errorCode, latency, DateTimeOffset.UtcNow);
        StorageConnectionHealth? previous = null;
        _health.AddOrUpdate(id, current, (_, existing) =>
        {
            previous = existing;
            return current;
        });
        if (previous?.Healthy != healthy)
            _connectionObserver.HealthChanged(id, provider, previous, current);
    }

    /// <summary>
    /// Describes a registered connection and the server behind it: address, transport security, the
    /// certificate or host key presented, negotiated algorithms, server software and features, session
    /// pool counters, and the last health check. Opens a session when the provider needs one to answer.
    /// </summary>
    /// <param name="connectionId">Connection to describe.</param>
    /// <param name="cancellationToken">Token used to cancel the request.</param>
    /// <returns>The diagnostics, or the error from opening a session.</returns>
    public async Task<Result<StorageConnectionDiagnostics>> GetConnectionDiagnosticsAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionId(connectionId);
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = AcquireOperation(connectionId);
        StorageConnectionInfo? info;
        lock (_registryGate)
            _connectionInfos.TryGetValue(lease.Backend.ConnectionId, out info);
        _health.TryGetValue(lease.Backend.ConnectionId, out var health);
        return await DiagnoseAsync(lease.Backend, info?.Host, info?.Port, info?.Security ?? StorageTransportSecurity.Unknown, health, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<Result<StorageConnectionDiagnostics>> DiagnoseAsync(
        IStorageBackend backend,
        string? host,
        int? port,
        StorageTransportSecurity security,
        StorageConnectionHealth? health,
        CancellationToken cancellationToken)
    {
        var diagnostics = new StorageConnectionDiagnostics
        {
            ConnectionId = backend.ConnectionId,
            Provider = backend.Provider,
            Host = host,
            Port = port,
            Security = security,
            LastHealth = health
        };
        if (backend is not IStorageDiagnosticsSource source)
            return Result<StorageConnectionDiagnostics>.Success(diagnostics);
        var details = await source.GetServerDetailsAsync(cancellationToken).ConfigureAwait(false);
        if (details.IsFailure)
            return Result<StorageConnectionDiagnostics>.Failure(details.Error!);
        return Result<StorageConnectionDiagnostics>.Success(diagnostics with
        {
            ServerSystem = details.Value!.System,
            ServerSoftware = details.Value.Software,
            ServerFeatures = details.Value.Features,
            Negotiated = details.Value.Negotiated,
            ServerIdentity = source.PresentedIdentity,
            Pool = source.PoolStats
        });
    }

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

    private const long DefaultTestBufferBytes = 1_048_576;

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

        await using var source = new LocalStorageBackend(
            ".cl-storage-directory-upload",
            new LocalConnectionConfig { RootPath = localRoot, FollowLinks = false },
            _storageConfig?.MaxBufferedDownloadBytes ?? LocalStorageBackend.DefaultMaxBufferedDownloadBytes);
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

    /// <summary>
    /// Downloads a complete storage directory into a caller-selected local directory with per-file
    /// atomic replacement and rollback of changes made by a failed transfer.
    /// </summary>
    public async Task<Result<StorageDirectoryTransferReport>> DownloadDirectoryAsync(
        string sourceConnectionId,
        string sourcePath,
        string destinationDirectoryPath,
        StorageTransferOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionId(sourceConnectionId);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new StorageTransferOptions();
        var validation = options.Validate();
        if (validation.IsFailure)
            return Result<StorageDirectoryTransferReport>.Failure(validation.Error!);
        var normalizedSource = StoragePath.Normalize(sourcePath);
        if (normalizedSource.IsFailure)
            return Result<StorageDirectoryTransferReport>.Failure(normalizedSource.Error!);
        if (string.IsNullOrWhiteSpace(destinationDirectoryPath))
            return Result<StorageDirectoryTransferReport>.Failure(StorageErrors.InvalidPath(
                "A local destination directory path is required."));

        string parent;
        string localDestinationName;
        try
        {
            var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectoryPath));
            if (File.Exists(destination))
                return Result<StorageDirectoryTransferReport>.Failure(StorageErrors.Conflict(
                    "The local directory destination is an existing file."));
            parent = Path.GetDirectoryName(destination) ?? string.Empty;
            localDestinationName = Path.GetFileName(destination);
            if (parent.Length == 0 || localDestinationName.Length == 0)
                return Result<StorageDirectoryTransferReport>.Failure(StorageErrors.InvalidPath(
                    "The local destination must identify a named directory below a parent directory."));
            if (options.CreateParents)
                Directory.CreateDirectory(parent);
            else if (!Directory.Exists(parent))
                return Result<StorageDirectoryTransferReport>.Failure(StorageErrors.NotFound(
                    "The local destination parent directory was not found."));
        }
        catch (Exception error)
        {
            return Result<StorageDirectoryTransferReport>.Failure(StorageErrors.FromException(
                error,
                "Resolve local download directory"));
        }

        await using var destinationBackend = new LocalStorageBackend(
            ".cl-storage-directory-download",
            new LocalConnectionConfig { RootPath = parent, FollowLinks = false },
            _storageConfig?.MaxBufferedDownloadBytes ?? LocalStorageBackend.DefaultMaxBufferedDownloadBytes);
        BackendEntry.BackendOperationLease? sourceLease = null;
        StorageEventPublisher? publisher = null;
        StorageDirectoryTransferReport? report = null;
        string? sourceId = null;
        StorageProvider sourceProvider = default;
        try
        {
            sourceLease = AcquireOperation(sourceConnectionId);
            sourceId = sourceLease.Backend.ConnectionId;
            sourceProvider = sourceLease.Backend.Provider;
            var sourceInfo = await sourceLease.Backend.GetInfoAsync(
                normalizedSource.Value!,
                cancellationToken).ConfigureAwait(false);
            if (sourceInfo.IsFailure)
                return Result<StorageDirectoryTransferReport>.Failure(sourceInfo.Error!);
            if (sourceInfo.Value!.ItemType != StorageItemType.Directory)
                return Result<StorageDirectoryTransferReport>.Failure(StorageErrors.Conflict(
                    "DownloadDirectoryAsync requires a storage directory source."));

            var copied = await StorageTransferCoordinator.CopyAsync(
                sourceLease.Backend,
                normalizedSource.Value!,
                destinationBackend,
                localDestinationName,
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
            sourceLease?.Dispose();
        }

        await publisher!.PublishAsync(new StorageDirectoryDownloadedEvent(
            sourceId!,
            sourceProvider,
            normalizedSource.Value!,
            report!.Files,
            report.Directories,
            report.Bytes,
            DateTimeOffset.UtcNow)).ConfigureAwait(false);
        return Result<StorageDirectoryTransferReport>.Success(report);
    }

    /// <summary>
    /// Moves a file or directory between mounted connections. The source is deleted only after the
    /// complete destination tree has committed successfully, and a single file only while it is still the
    /// version that was copied.
    /// </summary>
    /// <returns>What happened, including whether the source was deleted.</returns>
    public Task<StorageTransferReport> MoveAsync(
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
            move: true,
            cancellationToken);

    private async Task<StorageTransferReport> TransferAsync(
        string sourceConnectionId,
        string sourcePath,
        string destinationConnectionId,
        string destinationPath,
        StorageTransferOptions? options,
        bool move,
        CancellationToken cancellationToken)
    {
        ValidateConnectionId(sourceConnectionId);
        ValidateConnectionId(destinationConnectionId);
        options ??= new StorageTransferOptions();
        var report = new StorageTransferReport { Outcome = StorageTransferOutcome.Failed, SourcePath = sourcePath, DestinationPath = destinationPath };
        StorageTransferReport Fail(Error error) => report with { Outcome = StorageTransferOutcome.Failed, Error = error };
        // A transfer reports what happened rather than throwing; a cancel before it starts is reported too.
        if (cancellationToken.IsCancellationRequested)
            return report with
            {
                Outcome = StorageTransferOutcome.Cancelled,
                Error = StorageErrors.Cancelled("The transfer was cancelled before it started."),
                SourceDeleted = move ? false : null
            };

        var optionsValidation = options.Validate();
        if (optionsValidation.IsFailure)
            return Fail(optionsValidation.Error!);

        var normalizedSource = NormalizeTransferPath(sourcePath, "source");
        if (normalizedSource.IsFailure)
            return Fail(normalizedSource.Error!);
        var normalizedDestination = NormalizeTransferPath(destinationPath, "destination");
        if (normalizedDestination.IsFailure)
            return Fail(normalizedDestination.Error!);
        report = report with { SourcePath = normalizedSource.Value!, DestinationPath = normalizedDestination.Value! };

        var destinationTarget = normalizedDestination.Value!;
        BackendEntry.BackendOperationLease? sourceLease = null;
        BackendEntry.BackendOperationLease? destinationLease = null;
        StorageEventPublisher? publisher = null;
        StorageTransferSummary? summary = null;
        bool sameBackend = false;
        // A move that committed its destination but kept (part of) its source is announced as a copy, so
        // watchers and caches learn about the new destination.
        var announceAsCopy = false;
        string? effectiveSourceId = null;
        string? effectiveDestinationId = null;
        StorageProvider sourceProvider = default;
        StorageProvider destinationProvider = default;
        IStorageBackend? movedSource = null;
        TransferState? state = null;
        try
        {
            try
            {
                sourceLease = AcquireOperation(sourceConnectionId);
                destinationLease = AcquireOperation(destinationConnectionId);
            }
            catch (KeyNotFoundException error)
            {
                return Fail(StorageErrors.NotFound(error.Message));
            }
            var sourceBackend = sourceLease.Backend;
            var destinationBackend = destinationLease.Backend;
            sameBackend = ReferenceEquals(sourceBackend, destinationBackend);
            effectiveSourceId = sourceBackend.ConnectionId;
            effectiveDestinationId = destinationBackend.ConnectionId;
            sourceProvider = sourceBackend.Provider;
            destinationProvider = destinationBackend.Provider;
            var usedNativeOperation = false;
            var perFileDecisions = false;

            // Conditional conflict policies decide per file. A single file is decided here, so the
            // native operation can still run; a directory must relay so every file is decided on its own.
            // Resume is decided by the coordinator, which owns the resumable staging.
            // Rename picks the free name here only for a file the server can copy or move itself (a same-server
            // rename stays a rename); the native call then retries the next free name when the chosen one is taken
            // at commit. Anything that relays leaves Rename to the coordinator, which does the same for its promote.
            var nativeCandidate = sameBackend && !options.RequiresGuarantees &&
                options.MetadataPreservation != StorageMetadataPreservation.Discard;
            string? renamedFrom = null;
            var requestedOptions = options;
            if (options.ConflictPolicy == StorageConflictPolicy.Rename && !nativeCandidate)
            {
                perFileDecisions = true;
            }
            else if (StorageConflictResolver.IsConditional(options.ConflictPolicy) && options.ConflictPolicy != StorageConflictPolicy.Resume)
            {
                // A pinned version is judged by its own size and time, not the latest one's.
                var info = await StorageTransferCoordinator.ResolveSourceAsync(sourceBackend, normalizedSource.Value!, options.SourceVersionId, cancellationToken).ConfigureAwait(false);
                if (info.IsFailure)
                    return Fail(info.Error!);
                if (info.Value!.ItemType == StorageItemType.File)
                {
                    var decision = await StorageConflictResolver.ResolveAsync(
                        destinationBackend,
                        destinationTarget,
                        options.ConflictPolicy,
                        options.Overwrite,
                        info.Value.Size,
                        info.Value.LastModified,
                        cancellationToken).ConfigureAwait(false);
                    if (decision.IsFailure)
                        return Fail(decision.Error!);
                    if (decision.Value.Skip)
                    {
                        return report with
                        {
                            Outcome = StorageTransferOutcome.Skipped,
                            SourceType = StorageItemType.File,
                            SkippedFiles = 1,
                            SkipReason = StorageConflictResolver.SkipReasonFor(options.ConflictPolicy!.Value),
                            DestinationETag = decision.Value.Existing?.ETag,
                            DestinationVersionId = decision.Value.Existing?.VersionId,
                            SourceDeleted = move ? false : null
                        };
                    }
                    if (options.ConflictPolicy == StorageConflictPolicy.Rename)
                        renamedFrom = destinationTarget;
                    destinationTarget = decision.Value.Path;
                    options = options with { Overwrite = decision.Value.Overwrite, ConflictPolicy = null };
                }
                else
                {
                    perFileDecisions = true;
                }
            }

            // Guarantees (conditions, pinning, verification, resume) need the staged relay, not a native call, and
            // so does discarding metadata, which a server-side copy would carry.
            if (sameBackend && !perFileDecisions && !options.RequiresGuarantees &&
                options.MetadataPreservation != StorageMetadataPreservation.Discard)
            {
                var relationship = StorageTransferPath.ValidateDistinct(
                    normalizedSource.Value!,
                    destinationTarget);
                if (relationship.IsFailure)
                    return Fail(relationship.Error!);

                var sourceInfo = await sourceBackend.GetInfoAsync(
                    normalizedSource.Value!,
                    cancellationToken).ConfigureAwait(false);
                if (sourceInfo.IsFailure)
                    return Fail(sourceInfo.Error!);
                if (sourceInfo.Value!.ItemType == StorageItemType.Directory)
                {
                    relationship = StorageTransferPath.ValidateDirectoryDestination(
                        normalizedSource.Value!,
                        destinationTarget);
                    if (relationship.IsFailure)
                        return Fail(relationship.Error!);
                }

                var requiredFeatures = (move, sourceInfo.Value.ItemType) switch
                {
                    (false, StorageItemType.File) => StorageFeature.FileCopy | StorageFeature.ServerSideCopy,
                    // Directory copy implementations commonly loop over provider objects and can
                    // leave a partial tree. The coordinator tracks and rolls back every new item.
                    (false, StorageItemType.Directory) => StorageFeature.None,
                    (true, StorageItemType.File) => StorageFeature.FileMove | StorageFeature.ServerSideMove,
                    (true, StorageItemType.Directory) => StorageFeature.DirectoryMove |
                        StorageFeature.ServerSideMove |
                        StorageFeature.AtomicMove,
                    _ => StorageFeature.None
                };
                var supportsNativeOperation = requiredFeatures != StorageFeature.None &&
                    sourceBackend.Capabilities.Supports(requiredFeatures);
                var nativeOptions = options;
                if (supportsNativeOperation && move && sourceInfo.Value.ItemType == StorageItemType.Directory)
                {
                    // A native directory move replaces or refuses an existing destination depending on the server;
                    // onto an existing directory the relay runs instead, which merges.
                    var destinationExists = await destinationBackend.ExistsAsync(destinationTarget, cancellationToken).ConfigureAwait(false);
                    if (destinationExists.IsFailure)
                        return Fail(destinationExists.Error!);
                    supportsNativeOperation = !destinationExists.Value;
                }
                // A move that is not an atomic rename (a copy and a delete) is pinned to the version read here, so a
                // write made in between is neither copied half-way nor deleted. Where the source has no identity to
                // pin, or the connection refuses the pin (WebDAV), the source is compared immediately before the
                // server's own move instead of relaying the bytes through the client, and the report says so.
                var checkSourceBefore = false;
                if (supportsNativeOperation && move && sourceInfo.Value.ItemType == StorageItemType.File &&
                    !sourceBackend.Capabilities.Supports(StorageFeature.AtomicMove))
                {
                    if (sourceInfo.Value.ETag is { } eTag)
                        nativeOptions = options with { ExpectedSourceETag = eTag };
                    else if (sourceInfo.Value.VersionId is { } version)
                        nativeOptions = options with { SourceVersionId = version };
                    else
                        checkSourceBefore = true;
                }
                if (!supportsNativeOperation && renamedFrom is not null)
                {
                    // The server cannot do it: the relay decides the name, and retries the next one at its commit.
                    options = requestedOptions;
                    destinationTarget = renamedFrom;
                    renamedFrom = null;
                }
                if (supportsNativeOperation)
                {
                    if (options.PhaseChanged is { } committing)
                        await committing(Queue.StorageTransferPhase.Committing, cancellationToken).ConfigureAwait(false);
                    // A server-side operation moves no bytes through the client: report its start and end.
                    var total = sourceInfo.Value.ItemType == StorageItemType.File ? sourceInfo.Value.Size : null;
                    options.Progress?.Report(new StorageTransferProgress(0, total, false, ItemPath: normalizedSource.Value));
                    var native = await NativeAsync().ConfigureAwait(false);
                    // Rename: a name taken between the choice and the server's rename is not a failure; the next free
                    // one is used. Only a destination that is really there now counts as taken.
                    for (var retry = 0; renamedFrom is not null && retry < RenameRetries && native.IsFailure &&
                        native.Error!.Code == StorageErrors.ConflictCode && !StorageErrorInfo.DestinationCommitted(native.Error); retry++)
                    {
                        var taken = await destinationBackend.ExistsAsync(destinationTarget, cancellationToken).ConfigureAwait(false);
                        if (taken.IsFailure || !taken.Value)
                            break;
                        var next = await StorageConflictResolver.ResolveAsync(
                            destinationBackend, renamedFrom, StorageConflictPolicy.Rename, false, sourceInfo.Value.Size, sourceInfo.Value.LastModified, cancellationToken).ConfigureAwait(false);
                        if (next.IsFailure || next.Value.Path == destinationTarget)
                            break;
                        destinationTarget = next.Value.Path;
                        native = await NativeAsync().ConfigureAwait(false);
                    }

                    async Task<Result> NativeAsync()
                    {
                        if (checkSourceBefore)
                        {
                            var unchanged = await SourceUnchangedAsync(sourceBackend, normalizedSource.Value!, sourceInfo.Value!, cancellationToken).ConfigureAwait(false);
                            if (unchanged.IsFailure)
                                return unchanged;
                        }
                        var result = move
                            ? await sourceBackend.MoveAsync(normalizedSource.Value!, destinationTarget, nativeOptions, cancellationToken).ConfigureAwait(false)
                            : await sourceBackend.CopyAsync(normalizedSource.Value!, destinationTarget, nativeOptions, cancellationToken).ConfigureAwait(false);
                        if (result.IsFailure && result.Error!.Code == StorageErrors.UnsupportedCode && !ReferenceEquals(nativeOptions, options))
                        {
                            // The connection cannot pin its move to a version: the source is compared immediately
                            // before the server's move instead.
                            checkSourceBefore = true;
                            nativeOptions = options;
                            return await NativeAsync().ConfigureAwait(false);
                        }
                        return result;
                    }

                    var nativeCommitted = native.IsSuccess || StorageErrorInfo.DestinationCommitted(native.Error);
                    if (!nativeCommitted)
                    {
                        // A provider that stopped part-way (a WebDAV 207 multi-status) left a mixed state to reconcile.
                        return native.Error!.Code == StorageErrors.PartialFailureCode
                            ? report with { Outcome = StorageTransferOutcome.NeedsReconciliation, Error = native.Error, SourceDeleted = move ? false : null }
                            : Fail(native.Error!);
                    }
                    else
                    {
                        // Committed. The report is built first and only then completed, with reads that ignore the
                        // caller's cancel, so a cancel now cannot turn a finished copy or move into "Cancelled".
                        usedNativeOperation = true;
                        if (move)
                            movedSource = sourceBackend;
                        // A move whose source delete failed names the source itself; that is not an internal leftover.
                        var leftBehind = StagedWriter.LeftBehind(native.Error)
                            .Where(path => !string.Equals(path, normalizedSource.Value, StringComparison.Ordinal)).ToList();
                        report = report with
                        {
                            Outcome = StorageTransferOutcome.Completed,
                            SourceType = sourceInfo.Value.ItemType,
                            WrittenPath = destinationTarget,
                            Files = sourceInfo.Value.ItemType == StorageItemType.File ? 1 : 0,
                            Directories = sourceInfo.Value.ItemType == StorageItemType.Directory ? 1 : 0,
                            Bytes = sourceInfo.Value.Size ?? 0,
                            DestinationCommitted = true,
                            SourceDeleted = move ? true : null,
                            BackupLeftBehind = leftBehind.FirstOrDefault(IsBackupName),
                            StagingLeftBehind = leftBehind.FirstOrDefault(path => !IsBackupName(path))
                        };
                        publisher = CaptureEventPublisher();
                        await CompleteNativeReportAsync().ConfigureAwait(false);

                        async Task CompleteNativeReportAsync()
                        {
                            options.Progress?.Report(new StorageTransferProgress(total ?? 0, total ?? 0, true, ItemPath: normalizedSource.Value));
                            if (move && native.IsFailure)
                            {
                                // The provider committed the destination but reported a failure after it: whether the
                                // source is still there decides between a finished move and one to reconcile.
                                var sourceLeft = await TryExistsAsync(sourceBackend, normalizedSource.Value!).ConfigureAwait(false);
                                if (sourceLeft != false)
                                {
                                    announceAsCopy = true;
                                    report = report with { Outcome = StorageTransferOutcome.NeedsReconciliation, SourceDeleted = false, Error = native.Error };
                                }
                            }
                            if (sourceInfo.Value.ItemType == StorageItemType.File)
                            {
                                var committed = await TryGetInfoAsync(destinationBackend, destinationTarget).ConfigureAwait(false);
                                if (committed is not null)
                                    report = report with
                                    {
                                        Bytes = committed.Size ?? report.Bytes,
                                        DestinationETag = committed.ETag,
                                        DestinationVersionId = committed.VersionId
                                    };
                            }
                            else
                            {
                                // A server-side directory move names no files; they are counted where they landed.
                                var (files, directories, bytes) = await CountTreeAsync(destinationBackend, destinationTarget).ConfigureAwait(false);
                                report = report with { Files = files, Directories = directories + 1, Bytes = bytes };
                            }
                            var enforcement = StorageConditionEnforcement.None;
                            if (!options.Overwrite)
                                enforcement = await StorageConditionEnforcements.ForAsync(
                                    sourceBackend, StorageConditionKind.CreateOnly, serverSideCopy: true, CancellationToken.None).ConfigureAwait(false);
                            // The source compared just before the move is the weaker guarantee, and it is what is reported.
                            if (checkSourceBefore)
                                enforcement = StorageConditionEnforcement.CheckedBeforeCommit;
                            report = report with { ConditionEnforcement = enforcement };
                        }
                    }
                }
            }

            if (!usedNativeOperation)
            {
                // The relay reads the source and writes the destination at the same time. On one connection
                // limited to a single session that cannot work; it fails now instead of waiting out the
                // session timeout.
                if (sameBackend && StorageTransferPipeline.SessionLimitFor(sourceBackend) is < 2)
                    return Fail(StorageErrors.Unsupported(
                        "This transfer relays through the client on one connection, which needs two sessions (one to read, one to write), but the connection allows one. Raise Session.MaxSessions to at least 2.",
                        "requiredSessions=2;maxSessions=1"));

                state = new TransferState();
                var copied = await StorageTransferCoordinator.CopyAsync(
                    sourceBackend,
                    normalizedSource.Value!,
                    destinationBackend,
                    destinationTarget,
                    options,
                    cancellationToken,
                    state).ConfigureAwait(false);
                if (copied.IsFailure)
                {
                    report = FailedTransferReport(report, copied.Error!, state, move);
                    if (!report.DestinationCommitted)
                        return report;
                    // Committed but not as it should be: still announced, so watchers see the new destination.
                    announceAsCopy = true;
                    publisher = CaptureEventPublisher();
                    summary = new StorageTransferSummary(report.SourceType ?? StorageItemType.File, state.FilesCommitted, state.DirectoriesCreated, state.BytesCommitted);
                }
                else
                {
                    summary = copied.Value!;
                    report = SummaryReport(report, summary, move);
                    // A resumed move whose destination already holds the content (for example after a crash between
                    // commit and source deletion) still has to delete its source; any other skip leaves it.
                    var alreadyMoved = move && summary.SkipReason == StorageSkipReason.AlreadyComplete;
                    if (summary.SkippedFiles > 0 && summary.Files == 0 && summary.SourceType is StorageItemType.File or StorageItemType.Link && !alreadyMoved)
                        return report with { Outcome = StorageTransferOutcome.Skipped, SourceDeleted = move ? false : null };

                    report = report with { Outcome = StorageTransferOutcome.Completed, SourceDeleted = move ? false : null };
                    // Announced even if deleting the source fails below: the destination is committed.
                    publisher = CaptureEventPublisher();
                    if (move)
                        movedSource = sourceBackend;
                    if (move && options.PhaseChanged is { } deleting)
                        await deleting(Queue.StorageTransferPhase.DeletingSource, cancellationToken).ConfigureAwait(false);
                    if (move && summary.SourceType == StorageItemType.Directory)
                    {
                        var removed = await DeleteMovedDirectoryAsync(sourceBackend, normalizedSource.Value!, summary, state, cancellationToken).ConfigureAwait(false);
                        report = report with { SourceDeleted = removed.SourceDeleted };
                        if (removed.Error is not null)
                        {
                            announceAsCopy = true;
                            report = report with { Outcome = StorageTransferOutcome.NeedsReconciliation, Error = removed.Error };
                        }
                    }
                    else if (move)
                    {
                        var deleted = await DeleteIfUnchangedAsync(sourceBackend, normalizedSource.Value!, state.Source, ignoreMissing: false, cancellationToken).ConfigureAwait(false);
                        if (deleted.IsFailure)
                        {
                            announceAsCopy = true;
                            report = report with
                            {
                                Outcome = StorageTransferOutcome.NeedsReconciliation,
                                SourceDeleted = false,
                                Error = StorageErrors.PartialFailure(
                                    "The destination completed, but the source could not be deleted.",
                                    $"sourceDeleteError={deleted.Error!.Code};{StorageErrorInfo.DestinationStateKey}=complete")
                            };
                        }
                        else
                        {
                            report = report with { SourceDeleted = true };
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Reported rather than thrown, so the caller learns what the transfer left behind; a destination that
            // was already committed is still announced below.
            report = await StoppedReportAsync(StorageErrors.Cancelled("The transfer was cancelled.")).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A provider that throws is reported like one that fails.
            report = await StoppedReportAsync(StorageErrors.FromException(error, "Transfer")).ConfigureAwait(false);
        }
        finally
        {
            destinationLease?.Dispose();
            sourceLease?.Dispose();
        }

        await PublishTransferResultAsync(
            Result.Success(),
            publisher,
            sameBackend,
            move && !announceAsCopy,
            effectiveSourceId!,
            sourceProvider,
            report.SourcePath,
            effectiveDestinationId!,
            destinationProvider,
            report.WrittenPath ?? destinationTarget,
            summary ?? new StorageTransferSummary(report.SourceType ?? StorageItemType.File, report.Files, report.Directories, report.Bytes)).ConfigureAwait(false);
        return report;

        // A cancel or an exception: what was already done is reported as it is. Before the commit that is the
        // relay's staging, backup and resume state; after it, the committed destination and how much of a moved
        // source is already gone (read again, as the delete may have been under way).
        async Task<StorageTransferReport> StoppedReportAsync(Error error)
        {
            if (!report.DestinationCommitted)
            {
                var stopped = state is null
                    ? report with { Outcome = error.Code == StorageErrors.CancelledCode ? StorageTransferOutcome.Cancelled : StorageTransferOutcome.Failed, Error = error, SourceDeleted = move ? false : null }
                    : FailedTransferReport(report, error, state, move);
                if (stopped.DestinationCommitted)
                {
                    announceAsCopy = true;
                    publisher ??= CaptureEventPublisher();
                    summary ??= new StorageTransferSummary(stopped.SourceType ?? StorageItemType.File, state!.FilesCommitted, state.DirectoriesCreated, state.BytesCommitted);
                }
                return stopped;
            }
            var sourceDeleted = false;
            if (move && movedSource is not null)
                sourceDeleted = await IsGoneAsync(movedSource, report.SourcePath).ConfigureAwait(false);
            publisher ??= CaptureEventPublisher();
            if (!move || sourceDeleted)
                return report with { Outcome = StorageTransferOutcome.Completed, Error = null, SourceDeleted = move ? true : null };
            announceAsCopy = true;
            var details = $"{StorageErrorInfo.DestinationStateKey}=complete";
            if (state is { SourceItemsDeleted: > 0 })
                details += $";sourceItemsDeleted={state.SourceItemsDeleted}";
            return report with
            {
                Outcome = StorageTransferOutcome.NeedsReconciliation,
                Error = StagedWriter.AppendDetails(error, details),
                SourceDeleted = false
            };
        }
    }

    /// <summary>How often a Rename tries the next free name when its chosen one is taken at the server's rename.</summary>
    private const int RenameRetries = 8;

    /// <summary>
    /// Succeeds when the source is still the version <paramref name="read"/> describes, read immediately before
    /// a native move that cannot be pinned to it.
    /// </summary>
    private static async Task<Result> SourceUnchangedAsync(IStorageBackend source, string path, StorageItem read, CancellationToken cancellationToken)
    {
        var current = await source.GetInfoAsync(path, cancellationToken).ConfigureAwait(false);
        if (current.IsFailure)
            return Result.Failure(current.Error!);
        return current.Value!.ItemType == read.ItemType && current.Value.Size == read.Size &&
            current.Value.LastModified == read.LastModified && StorageTransferCoordinator.SameVersion(read, current.Value)
            ? Result.Success()
            : Result.Failure(StorageErrors.Conflict($"The source '{path}' changed after it was read, so it was not moved."));
    }

    /// <summary>Counts the files, folders and bytes below a directory, ignoring the caller's cancel; zeros when it cannot be listed.</summary>
    private static async Task<(long Files, long Directories, long Bytes)> CountTreeAsync(IStorageBackend backend, string path)
    {
        long files = 0, directories = 0, bytes = 0;
        try
        {
            await foreach (var item in backend.EnumerateItemsAsync(path, new StorageListOptions { Recursive = true }, CancellationToken.None).ConfigureAwait(false))
            {
                if (item.IsFailure) break;
                if (item.Value!.ItemType == StorageItemType.Directory)
                    directories++;
                else
                {
                    files++;
                    bytes += item.Value.Size ?? 0;
                }
            }
        }
        catch (Exception)
        {
            // Only the report's counts depend on it.
        }
        return (files, directories, bytes);
    }

    /// <summary>Whether a provider's leftover is a backup rather than a staging copy.</summary>
    private static bool IsBackupName(string path) => path.Contains("backup", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads an item after a commit, ignoring the caller's cancel; null when it cannot be read.</summary>
    private static async Task<StorageItem?> TryGetInfoAsync(IStorageBackend backend, string path)
    {
        try
        {
            var info = await backend.GetInfoAsync(path, CancellationToken.None).ConfigureAwait(false);
            return info.IsSuccess ? info.Value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Whether an item exists, ignoring the caller's cancel; null when that cannot be told.</summary>
    private static async Task<bool?> TryExistsAsync(IStorageBackend backend, string path)
    {
        try
        {
            var exists = await backend.ExistsAsync(path, CancellationToken.None).ConfigureAwait(false);
            return exists.IsSuccess ? exists.Value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Whether an item is provably gone (its read answers not-found), ignoring the caller's cancel.</summary>
    private static async Task<bool> IsGoneAsync(IStorageBackend backend, string path)
    {
        try
        {
            var info = await backend.GetInfoAsync(path, CancellationToken.None).ConfigureAwait(false);
            return info.IsFailure && info.Error!.Code == StorageErrors.NotFoundCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Folds a coordinator summary into the report.</summary>
    private static StorageTransferReport SummaryReport(StorageTransferReport report, StorageTransferSummary summary, bool move) => report with
    {
        SourceType = summary.SourceType,
        Files = summary.Files,
        Directories = summary.Directories,
        Bytes = summary.Bytes,
        BytesResumed = summary.BytesResumed,
        SkippedFiles = summary.SkippedFiles,
        WrittenPath = summary.WrittenPath ?? (summary.SourceType == StorageItemType.File ? null : report.DestinationPath),
        SkipReason = summary.SkipReason,
        Sha256 = summary.Sha256,
        VerifiedBy = summary.VerifiedBy,
        DestinationETag = summary.Destination?.ETag,
        DestinationVersionId = summary.Destination?.VersionId,
        // A directory transfer that only reused existing folders and skipped every file wrote nothing.
        DestinationCommitted = summary.Files > 0 || summary.CreatedDirectories > 0,
        ConditionEnforcement = summary.ConditionEnforcement,
        StagingLeftBehind = summary.StagingLeftBehind,
        BackupLeftBehind = summary.BackupLeftBehind,
        SourceDeleted = move ? false : null
    };

    /// <summary>Builds the report for a coordinator failure from what it recorded before stopping.</summary>
    private static StorageTransferReport FailedTransferReport(StorageTransferReport report, Error error, TransferState state, bool move)
    {
        // An error raised after the destination committed (for example a destination that does not hold what
        // was committed) is never a clean failure.
        var committed = state.DestinationCommitted || StorageErrorInfo.DestinationCommitted(error);
        var mixed = committed || error.Code == StorageErrors.PartialFailureCode || state.RollbackIncomplete || state.BackupLeftBehind is not null ||
            (state.StagingLeftBehind is not null && !state.StagingResumable);
        StorageResumeToken? token = null;
        if (state.StagingResumable && state.StagingLeftBehind is { } staging && !committed)
        {
            token = new StorageResumeToken
            {
                DestinationPath = report.DestinationPath,
                StagingPath = staging,
                BytesStaged = state.BytesStaged,
                SourcePath = state.Source?.Path,
                SourceETag = state.Source?.ETag,
                SourceVersionId = state.Source?.VersionId,
                SourceLength = state.Source?.Size,
                SourceLastModified = state.Source?.LastModified
            };
        }
        return report with
        {
            Outcome = mixed ? StorageTransferOutcome.NeedsReconciliation
                : error.Code == StorageErrors.CancelledCode ? StorageTransferOutcome.Cancelled
                : StorageTransferOutcome.Failed,
            Error = error,
            SourceType = state.Source?.ItemType,
            DestinationCommitted = committed,
            SourceDeleted = move ? false : null,
            StagingLeftBehind = state.StagingLeftBehind,
            BackupRestored = state.BackupRestored,
            BackupLeftBehind = state.BackupLeftBehind,
            ConditionEnforcement = state.ConditionEnforcement,
            ResumeToken = token
        };
    }

    /// <summary>
    /// Deletes a moved source file only while it is still the version that was copied: atomically where the
    /// source supports conditional deletes, otherwise by comparing its identity immediately before.
    /// </summary>
    private static async Task<Result> DeleteIfUnchangedAsync(
        IStorageBackend source,
        string path,
        StorageItem? copied,
        bool ignoreMissing,
        CancellationToken cancellationToken)
    {
        StorageMutationCondition? condition = null;
        if (copied is not null)
        {
            if (source.Capabilities.Supports(StorageFeature.ConditionalDelete) && (copied.ETag is not null || copied.VersionId is not null))
            {
                condition = new StorageMutationCondition { ExpectedETag = copied.ETag, ExpectedVersionId = copied.VersionId };
            }
            else
            {
                var current = await source.GetInfoAsync(path, cancellationToken).ConfigureAwait(false);
                if (current.IsFailure)
                    return ignoreMissing && current.Error!.Code == StorageErrors.NotFoundCode ? Result.Success() : Result.Failure(current.Error!);
                if (current.Value!.ItemType != copied.ItemType || current.Value.Size != copied.Size || current.Value.LastModified != copied.LastModified ||
                    (copied.VersionId is not null && copied.VersionId != current.Value.VersionId) ||
                    (copied.ETag is not null && !StagedWriter.SameETag(copied.ETag, current.Value.ETag)))
                    return Result.Failure(StorageErrors.Conflict($"The source '{path}' changed after it was copied, so it was not deleted."));
            }
        }
        return await source.DeleteAsync(
            path,
            new StorageDeleteOptions { IgnoreMissing = ignoreMissing, Condition = condition },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Completes a directory move: deletes each transferred source file only while it is still the version
    /// listed for the copy, then removes folders left empty, deepest first, never recursively. Files skipped on
    /// purpose stay, and so does anything that changed or appeared during the copy — which the result reports,
    /// together with whether the source directory is gone.
    /// </summary>
    private static async Task<(bool SourceDeleted, Error? Error)> DeleteMovedDirectoryAsync(
        IStorageBackend source,
        string sourceRoot,
        StorageTransferSummary summary,
        TransferState state,
        CancellationToken cancellationToken)
    {
        var kept = new HashSet<string>(StringComparer.Ordinal);
        string? firstError = null;
        foreach (var item in summary.TransferredItems ?? [])
        {
            var deleted = await DeleteIfUnchangedAsync(source, item.Path, item, ignoreMissing: true, cancellationToken).ConfigureAwait(false);
            if (deleted.IsFailure)
            {
                kept.Add(item.Path);
                firstError ??= deleted.Error!.Code;
            }
            else
            {
                state.SourceItemsDeleted++;
            }
        }

        var skipped = new HashSet<string>(summary.SkippedSources ?? [], StringComparer.Ordinal);
        var directories = new List<string>();
        var appeared = 0;
        await foreach (var entry in source.EnumerateItemsAsync(sourceRoot, new StorageListOptions { Recursive = true }, cancellationToken).ConfigureAwait(false))
        {
            if (entry.IsFailure)
            {
                firstError ??= entry.Error!.Code;
                break;
            }
            var path = StoragePath.Normalize(entry.Value!.Path);
            var normalized = path.IsSuccess ? path.Value! : entry.Value.Path;
            if (entry.Value.ItemType == StorageItemType.Directory)
                directories.Add(normalized);
            else if (!skipped.Contains(normalized) && !kept.Contains(normalized))
                appeared++;
        }
        directories.Add(sourceRoot);
        foreach (var directory in directories.Distinct(StringComparer.Ordinal).OrderByDescending(path => path.Count(c => c == '/')).ThenByDescending(path => path.Length))
        {
            // Non-recursive: a folder that still holds something fails with a conflict and stays.
            _ = await source.DeleteAsync(directory, new StorageDeleteOptions { IgnoreMissing = true }, cancellationToken).ConfigureAwait(false);
        }
        var rootLeft = await source.ExistsAsync(sourceRoot, cancellationToken).ConfigureAwait(false);
        var sourceDeleted = rootLeft.IsSuccess && !rootLeft.Value;
        if (kept.Count == 0 && appeared == 0 && firstError is null)
            return (sourceDeleted, null);
        return (sourceDeleted, StorageErrors.PartialFailure(
            $"The destination completed, but {kept.Count + appeared} source item(s) changed or appeared during the move and were kept at the source.",
            $"sourceChanged={kept.Count};sourceAdded={appeared};sourceDeleteError={firstError};{StorageErrorInfo.DestinationStateKey}=complete"));
    }

    private static Result<string> NormalizeTransferPath(string path, string role)
    {
        var normalized = StoragePath.Normalize(path);
        if (normalized.IsFailure)
            return normalized;
        return normalized.Value!.Length == 0
            ? Result<string>.Failure(StorageErrors.InvalidPath(
                $"A non-root transfer {role} path is required."))
            : normalized;
    }

    private static async Task<Result> PublishTransferResultAsync(
        Result result,
        StorageEventPublisher? publisher,
        bool sameBackend,
        bool move,
        string sourceConnectionId,
        StorageProvider sourceProvider,
        string sourcePath,
        string destinationConnectionId,
        StorageProvider destinationProvider,
        string destinationPath,
        StorageTransferSummary? summary)
    {
        if (result.IsFailure || publisher is null)
            return result;

        if (sameBackend)
        {
            if (move)
            {
                await publisher.PublishAsync(new StorageItemMovedEvent(
                    sourceConnectionId,
                    sourceProvider,
                    sourcePath,
                    destinationPath,
                    DateTimeOffset.UtcNow)).ConfigureAwait(false);
            }
            else
            {
                await publisher.PublishAsync(new StorageItemCopiedEvent(
                    sourceConnectionId,
                    sourceProvider,
                    sourcePath,
                    destinationPath,
                    DateTimeOffset.UtcNow)).ConfigureAwait(false);
            }
        }
        else if (move)
        {
            await publisher.PublishAsync(new StorageCrossConnectionMoveCompletedEvent(
                sourceConnectionId,
                sourceProvider,
                sourcePath,
                destinationConnectionId,
                destinationProvider,
                destinationPath,
                summary!.Files,
                summary.Directories,
                summary.Bytes,
                DateTimeOffset.UtcNow)).ConfigureAwait(false);
        }
        else
        {
            await publisher.PublishAsync(new StorageCrossConnectionCopyCompletedEvent(
                sourceConnectionId,
                sourceProvider,
                sourcePath,
                destinationConnectionId,
                destinationProvider,
                destinationPath,
                summary!.Files,
                summary.Directories,
                summary.Bytes,
                DateTimeOffset.UtcNow)).ConfigureAwait(false);
        }
        return result;
    }

    /// <summary>Compares two directory trees on (possibly different) connections.</summary>
    /// <param name="sourceConnectionId">Source connection.</param>
    /// <param name="sourcePath">Source directory.</param>
    /// <param name="destinationConnectionId">Destination connection.</param>
    /// <param name="destinationPath">Destination directory.</param>
    /// <param name="options">Comparison criteria.</param>
    /// <param name="cancellationToken">Token used to cancel the comparison.</param>
    /// <returns>Every path on either side with its relation.</returns>
    public Task<Result<Sync.StorageDiff>> CompareAsync(
        string sourceConnectionId,
        string sourcePath,
        string destinationConnectionId,
        string destinationPath,
        Sync.StorageCompareOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Sync.StorageSync.CompareAsync(GetStorage(sourceConnectionId), sourcePath, GetStorage(destinationConnectionId), destinationPath, options, cancellationToken);

    /// <summary>Synchronizes two directory trees on (possibly different) connections.</summary>
    /// <param name="sourceConnectionId">Source connection.</param>
    /// <param name="sourcePath">Source directory.</param>
    /// <param name="destinationConnectionId">Destination connection.</param>
    /// <param name="destinationPath">Destination directory.</param>
    /// <param name="options">Direction, deletes, dry run, and comparison settings.</param>
    /// <param name="cancellationToken">Token used to cancel the sync.</param>
    /// <returns>The steps taken, or planned for a dry run.</returns>
    public Task<Result<Sync.StorageSyncReport>> SyncAsync(
        string sourceConnectionId,
        string sourcePath,
        string destinationConnectionId,
        string destinationPath,
        Sync.StorageSyncOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Sync.StorageSync.SyncAsync(GetStorage(sourceConnectionId), sourcePath, GetStorage(destinationConnectionId), destinationPath, options, cancellationToken);

    /// <summary>Plans a sync between two connections without changing anything; approve the plan by its digest.</summary>
    /// <param name="sourceConnectionId">Source connection.</param>
    /// <param name="sourcePath">Source directory.</param>
    /// <param name="destinationConnectionId">Destination connection.</param>
    /// <param name="destinationPath">Destination directory.</param>
    /// <param name="options">Direction, conflicts, baseline, filters, and safety limits.</param>
    /// <param name="cancellationToken">Token used to cancel the listings.</param>
    /// <returns>The plan.</returns>
    public Task<Result<Sync.StorageSyncPlan>> PlanSyncAsync(
        string sourceConnectionId,
        string sourcePath,
        string destinationConnectionId,
        string destinationPath,
        Sync.StorageSyncOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Sync.StorageSync.PlanSyncAsync(GetStorage(sourceConnectionId), sourcePath, GetStorage(destinationConnectionId), destinationPath, options, cancellationToken);

    /// <summary>Applies an approved sync plan between two connections.</summary>
    /// <param name="sourceConnectionId">Source connection.</param>
    /// <param name="sourcePath">Source directory; must be the plan's.</param>
    /// <param name="destinationConnectionId">Destination connection.</param>
    /// <param name="destinationPath">Destination directory; must be the plan's.</param>
    /// <param name="plan">The plan.</param>
    /// <param name="approvedDigest">The digest that was approved.</param>
    /// <param name="options">The options the plan was made with.</param>
    /// <param name="cancellationToken">Stops the run; the report keeps what was done.</param>
    /// <returns>Each step's outcome.</returns>
    public Task<Result<Sync.StorageSyncReport>> ApplySyncAsync(
        string sourceConnectionId,
        string sourcePath,
        string destinationConnectionId,
        string destinationPath,
        Sync.StorageSyncPlan plan,
        string approvedDigest,
        Sync.StorageSyncOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Sync.StorageSync.ApplySyncAsync(GetStorage(sourceConnectionId), sourcePath, GetStorage(destinationConnectionId), destinationPath, plan, approvedDigest, options, cancellationToken);

    /// <summary>
    /// Opens a background transfer queue. Its jobs live in <see cref="Queue.StorageTransferQueueOptions.Store"/>
    /// (in memory by default); a durable store brings back the jobs of an earlier run, with those left
    /// running either queued again (when their destination was never touched) or marked interrupted.
    /// Stopping the library disposes the queue first, so its running jobs stop the same way.
    /// </summary>
    /// <param name="options">Limits, retries, and the job store.</param>
    /// <param name="cancellationToken">Token used to cancel loading the store.</param>
    /// <returns>The queue, or why the options are invalid.</returns>
    public async Task<Result<Queue.StorageTransferQueue>> OpenTransferQueueAsync(
        Queue.StorageTransferQueueOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureOperational();
        var opened = await Queue.StorageTransferQueue.OpenAsync(this, options ?? new Queue.StorageTransferQueueOptions(), cancellationToken).ConfigureAwait(false);
        if (opened.IsSuccess && !TrackQueue(opened.Value!))
        {
            // The library began stopping while the queue opened, after it had stopped the queues it knew of.
            await opened.Value!.DisposeAsync().ConfigureAwait(false);
            return Result<Queue.StorageTransferQueue>.Failure(StorageErrors.Unavailable("The storage library stopped while the transfer queue was opening."));
        }
        return opened;
    }

    /// <summary>Keeps a queue to stop with the library; false when the library is already stopping.</summary>
    private bool TrackQueue(Queue.StorageTransferQueue queue)
    {
        lock (_stateGate)
        {
            if (_state is LifecycleState.Stopping or LifecycleState.Stopped or LifecycleState.Disposed) return false;
            lock (_queues) _queues.Add(queue);
            return true;
        }
    }

    /// <summary>Forgets a queue that was disposed.</summary>
    internal void UntrackQueue(Queue.StorageTransferQueue queue)
    {
        lock (_queues) _queues.Remove(queue);
    }

    /// <summary>Returns an immutable snapshot containing sanitized connection information.</summary>
    public IReadOnlyList<StorageConnectionInfo> GetConnections()
    {
        EnsureOperational();
        lock (_registryGate)
        {
            var snapshot = _connectionInfos.Values
                .Select(connection => _health.TryGetValue(connection.Id, out var health) ? connection with { LastHealth = health } : connection)
                .OrderBy(connection => connection.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new ReadOnlyCollection<StorageConnectionInfo>(snapshot);
        }
    }

    /// <summary>
    /// Returns a reusable native client owned by the effective backend. A client already returned by this
    /// method becomes invalid when its connection is replaced or removed and must not be disposed by callers.
    /// </summary>
    public TClient GetNativeClient<TClient>(string connectionId = "Default") where TClient : class
    {
        ValidateConnectionId(connectionId);
        using var lease = AcquireOperation(connectionId);
        if (lease.Backend.TryGetNativeClient<TClient>(out var client))
            return client;
        throw new InvalidOperationException(
            $"Storage connection '{connectionId}' does not expose a reusable native client of type '{typeof(TClient).FullName}'.");
    }

    /// <summary>Opens a provider-native session whose lease retains the backend until disposal.</summary>
    public async Task<Result<NativeConnectionLease<TClient>>> OpenNativeConnectionAsync<TClient>(
        string connectionId = "Default",
        CancellationToken cancellationToken = default)
        where TClient : class
    {
        ValidateConnectionId(connectionId);
        cancellationToken.ThrowIfCancellationRequested();
        var entryLease = AcquireOperation(connectionId);
        try
        {
            var result = await entryLease.Backend.OpenNativeConnectionAsync<TClient>(cancellationToken).ConfigureAwait(false);
            if (result.IsFailure)
            {
                entryLease.Dispose();
                return Result<NativeConnectionLease<TClient>>.Failure(result.Error!);
            }

            var backendLease = result.Value!;
            return Result<NativeConnectionLease<TClient>>.Success(new NativeConnectionLease<TClient>(
                backendLease.Client,
                async _ =>
                {
                    try { await backendLease.DisposeAsync().ConfigureAwait(false); }
                    finally { entryLease.Dispose(); }
                }));
        }
        catch
        {
            entryLease.Dispose();
            throw;
        }
    }

    /// <summary>Adds or replaces a built-in typed connection and optionally persists its JSON section.</summary>
    public async Task<Result> AddOrUpdateConnectionAsync<TConfig>(
        string id,
        TConfig config,
        bool persist = true,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionId(id);
        ArgumentNullException.ThrowIfNull(config);
        var runtime = CaptureRuntimeSnapshot();
        cancellationToken.ThrowIfCancellationRequested();
        if (config is not LocalConnectionConfig localConnection)
        {
            if (config is StorageConnectionConfigBase providerConnection)
                return await AddOrUpdateProviderConnectionAsync(
                    id,
                    providerConnection,
                    persist,
                    runtime,
                    cancellationToken).ConfigureAwait(false);
            return Result.Failure(StorageErrors.Unsupported(
                $"Connection configuration type '{typeof(TConfig).Name}' does not have a provider factory in this version."));
        }

        var validation = localConnection.Validate();
        if (!validation.IsValid)
            return Result.Failure(StorageErrors.InvalidContent(
                $"Local storage connection '{id}' is invalid: {string.Join("; ", validation.Errors)}"));

        BackendEntry? replacement = null;
        if (localConnection.Enabled)
        {
            var built = await BuildHealthyLocalReplacementAsync(
                id,
                localConnection,
                runtime,
                cancellationToken).ConfigureAwait(false);
            if (built.IsFailure)
                return Result.Failure(built.Error!);
            replacement = built.Value!;
        }

        BackendEntry? previous = null;
        var gateEntered = false;
        try
        {
            await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateEntered = true;
            EnsureOperational();

            var conflictingSection = FindConfiguredNonLocalSection(id, runtime.Context);
            if (conflictingSection is not null)
                return Result.Failure(StorageErrors.Conflict(
                    $"Storage connection ID '{id}' is already configured in '{conflictingSection}'. IDs are case-insensitive."));

            if (persist)
            {
                var candidate = CloneLocalConfig(_persistedLocalConfig!);
                RemoveKey(candidate.Connections, id);
                candidate.Connections[id] = Clone(localConnection);
                try
                {
                    await SaveConfigAsync(runtime.Context, candidate).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    runtime.Logger.Error($"Failed to persist local storage connection '{id}'.", error);
                    return Result.Failure(StorageErrors.ProviderError(
                        $"Could not persist local storage connection '{id}'."));
                }
                _persistedLocalConfig = CloneLocalConfig(candidate);
                _runtimeLocalOverrides.Remove(id);
                ApplyRuntimeLocalOverrides(candidate);
                _localConfig = candidate;
            }
            else
            {
                _runtimeLocalOverrides[id] = Clone(localConnection);
                SetLocalConnection(_localConfig!, id, localConnection);
            }

            lock (_registryGate)
            {
                if (_registry.TryGetValue(id, out previous))
                    _registry.Remove(id);
                if (replacement is not null)
                    _registry[id] = replacement;
                _connectionInfos[id] = new StorageConnectionInfo(id, StorageProvider.Local, localConnection.RootPath, localConnection.Enabled);
                _health.TryRemove(id, out _);
            }
            replacement = null;
        }
        catch (OperationCanceledException) { throw; }
        finally
        {
            if (gateEntered)
                _mutationGate.Release();
            if (replacement is not null)
                await replacement.RetireAndDisposeAsync().ConfigureAwait(false);
        }

        if (previous is not null)
            await previous.RetireAndDisposeAsync().ConfigureAwait(false);
        return Result.Success();
    }

    private async Task<Result> AddOrUpdateProviderConnectionAsync(
        string id,
        StorageConnectionConfigBase connection,
        bool persist,
        RuntimeSnapshot runtime,
        CancellationToken cancellationToken)
    {
        var descriptor = DescribeProviderConnection(connection.GetType());
        if (descriptor is null || !_factories.ContainsKey(connection.GetType()))
            return Result.Failure(StorageErrors.Unsupported(
                $"Connection configuration type '{connection.GetType().Name}' does not have a provider factory."));

        var errors = connection.GetValidationErrors().ToArray();
        if (errors.Length > 0)
            return Result.Failure(StorageErrors.InvalidContent(
                $"{descriptor.Value.Provider} storage connection '{id}' is invalid: {string.Join("; ", errors)}"));

        var effectiveConnection = CloneProviderConnection(connection);
        BackendEntry? replacement = null;
        if (effectiveConnection.Enabled)
        {
            var built = await BuildHealthyProviderReplacementAsync(
                id,
                effectiveConnection,
                descriptor.Value,
                runtime,
                cancellationToken).ConfigureAwait(false);
            if (built.IsFailure) return Result.Failure(built.Error!);
            replacement = built.Value!;
        }

        BackendEntry? previous = null;
        var gateEntered = false;
        try
        {
            await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateEntered = true;
            EnsureOperational();

            var conflictingSection = FindConfiguredSection(
                id,
                runtime.Context,
                exceptConnectionType: effectiveConnection.GetType());
            if (conflictingSection is not null)
                return Result.Failure(StorageErrors.Conflict(
                    $"Storage connection ID '{id}' is already configured in '{conflictingSection}'. IDs are case-insensitive."));
            lock (_registryGate)
            {
                if (_connectionInfos.TryGetValue(id, out var existing) && existing.Provider != descriptor.Value.Provider)
                    return Result.Failure(StorageErrors.Conflict(
                        $"Storage connection ID '{id}' is already registered for provider '{existing.Provider}'."));
            }

            if (persist)
            {
                var current = GetProviderConfig(runtime.Context, effectiveConnection.GetType());
                var candidate = current.DeepClone();
                candidate.SetConnection(id, effectiveConnection);
                try
                {
                    await SaveProviderConfigAsync(runtime.Context, candidate).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    runtime.Logger.Error($"Failed to persist {descriptor.Value.Provider} storage connection '{id}'.", error);
                    return Result.Failure(StorageErrors.ProviderError(
                        $"Could not persist {descriptor.Value.Provider} storage connection '{id}'."));
                }
            }

            lock (_registryGate)
            {
                if (_registry.TryGetValue(id, out previous)) _registry.Remove(id);
                if (replacement is not null) _registry[id] = replacement;
                _connectionInfos[id] = DescribeConnection(id, descriptor.Value.Provider, effectiveConnection);
                _health.TryRemove(id, out _);
            }
            replacement = null;
        }
        catch (OperationCanceledException) { throw; }
        finally
        {
            if (gateEntered) _mutationGate.Release();
            if (replacement is not null)
                await replacement.RetireAndDisposeAsync().ConfigureAwait(false);
        }

        if (previous is not null)
            await previous.RetireAndDisposeAsync().ConfigureAwait(false);
        return Result.Success();
    }

    private async Task<Result<BackendEntry>> BuildHealthyProviderReplacementAsync(
        string id,
        StorageConnectionConfigBase configuration,
        ProviderDescriptor descriptor,
        RuntimeSnapshot runtime,
        CancellationToken cancellationToken)
    {
        BackendEntry? replacement = null;
        Task<Result>? probeTask = null;
        var deferCleanup = false;
        try
        {
            replacement = new BackendEntry(
                WithLimits(_factories[configuration.GetType()].Create(
                    id,
                    configuration,
                    runtime.MaxBufferedDownloadBytes,
                    _connectionObserver), configuration, _libraryLimits),
                ownsBackend: true);
            var timeoutDuration = TimeSpan.FromSeconds(runtime.HealthCheckTimeoutSeconds);
            using var timeout = new CancellationTokenSource(timeoutDuration);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            Result health;
            try
            {
                probeTask = replacement.Backend.CheckHealthAsync(linked.Token);
                health = await probeTask.WaitAsync(timeoutDuration, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                deferCleanup = true;
                throw;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                deferCleanup = true;
                return Result<BackendEntry>.Failure(StorageErrors.Timeout(
                    $"Storage connection '{id}' timed out during replacement health check."));
            }
            catch (TimeoutException)
            {
                timeout.Cancel();
                deferCleanup = true;
                return Result<BackendEntry>.Failure(StorageErrors.Timeout(
                    $"Storage connection '{id}' timed out during replacement health check."));
            }
            if (health.IsFailure) return Result<BackendEntry>.Failure(health.Error!);
            var result = replacement;
            replacement = null;
            return Result<BackendEntry>.Success(result);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return Result<BackendEntry>.Failure(StorageErrors.ProviderError(
                $"Could not build {descriptor.Provider} storage connection '{id}'."));
        }
        finally
        {
            if (replacement is not null)
            {
                var cleanup = ObserveProbeAndDisposeReplacementAsync(id, replacement, probeTask, runtime.Logger);
                if (deferCleanup || probeTask is { IsCompleted: false }) _ = cleanup;
                else await cleanup.ConfigureAwait(false);
            }
        }
    }

    private async Task<Result<BackendEntry>> BuildHealthyLocalReplacementAsync(
        string id,
        LocalConnectionConfig configuration,
        RuntimeSnapshot runtime,
        CancellationToken cancellationToken)
    {
        BackendEntry? replacement = null;
        Task<Result>? probeTask = null;
        var deferCleanup = false;
        var logger = runtime.Logger;
        try
        {
            replacement = new BackendEntry(
                WithLimits(_factories[typeof(LocalConnectionConfig)].Create(
                    id,
                    configuration,
                    runtime.MaxBufferedDownloadBytes), configuration, _libraryLimits),
                ownsBackend: true);

            var timeoutDuration = TimeSpan.FromSeconds(runtime.HealthCheckTimeoutSeconds);
            using var timeout = new CancellationTokenSource(timeoutDuration);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            Result health;
            try
            {
                probeTask = replacement.Backend.CheckHealthAsync(linked.Token);
                health = await probeTask
                    .WaitAsync(timeoutDuration, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                deferCleanup = true;
                throw;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                deferCleanup = true;
                return Result<BackendEntry>.Failure(StorageErrors.Timeout(
                    $"Storage connection '{id}' timed out during replacement health check."));
            }
            catch (TimeoutException)
            {
                timeout.Cancel();
                deferCleanup = true;
                return Result<BackendEntry>.Failure(StorageErrors.Timeout(
                    $"Storage connection '{id}' timed out during replacement health check."));
            }

            if (health.IsFailure)
                return Result<BackendEntry>.Failure(health.Error!);

            var result = replacement;
            replacement = null;
            return Result<BackendEntry>.Success(result);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return Result<BackendEntry>.Failure(StorageErrors.ProviderError(
                $"Could not build local storage connection '{id}'."));
        }
        finally
        {
            if (replacement is not null)
            {
                var cleanup = ObserveProbeAndDisposeReplacementAsync(id, replacement, probeTask, logger);
                if (deferCleanup || probeTask is { IsCompleted: false })
                    _ = cleanup;
                else
                    await cleanup.ConfigureAwait(false);
            }
        }
    }

    /// <summary>Removes an effective connection and, for local connections, optionally persists the removal.</summary>
    public async Task<Result> RemoveConnectionAsync(
        string id,
        bool persist = true,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionId(id);
        var runtime = CaptureRuntimeSnapshot();
        cancellationToken.ThrowIfCancellationRequested();
        BackendEntry? removed = null;
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOperational();
            bool exists;
            StorageConnectionInfo? info;
            lock (_registryGate)
                exists = _registry.ContainsKey(id) || _connectionInfos.TryGetValue(id, out info);
            var providerMatch = FindProviderConfigContaining(id, runtime.Context);
            exists = exists ||
                TryFindKey(_localConfig!.Connections, id, out _) ||
                TryFindKey(_persistedLocalConfig!.Connections, id, out _) ||
                providerMatch is not null;
            if (!exists)
                return Result.Failure(StorageErrors.NotFound($"Storage connection '{id}' is not registered."));

            var effectiveLocal = TryFindKey(_localConfig!.Connections, id, out _);
            var persistedLocal = TryFindKey(_persistedLocalConfig!.Connections, id, out var persistedKey);
            if (providerMatch is not null)
            {
                if (persist)
                {
                    var candidate = providerMatch.Config.DeepClone();
                    candidate.RemoveConnection(id);
                    try
                    {
                        await SaveProviderConfigAsync(runtime.Context, candidate).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error)
                    {
                        runtime.Logger.Error(
                            $"Failed to persist removal of {providerMatch.Descriptor.Provider} storage connection '{id}'.",
                            error);
                        return Result.Failure(StorageErrors.ProviderError(
                            $"Could not persist removal of {providerMatch.Descriptor.Provider} storage connection '{id}'."));
                    }
                }
            }
            else if (persist && (effectiveLocal || persistedLocal))
            {
                var candidate = CloneLocalConfig(_persistedLocalConfig);
                if (persistedKey is not null)
                    candidate.Connections.Remove(persistedKey);
                try
                {
                    await SaveConfigAsync(runtime.Context, candidate).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    runtime.Logger.Error($"Failed to persist removal of local storage connection '{id}'.", error);
                    return Result.Failure(StorageErrors.ProviderError(
                        $"Could not persist removal of local storage connection '{id}'."));
                }
                _persistedLocalConfig = CloneLocalConfig(candidate);
                _runtimeLocalOverrides.Remove(id);
                ApplyRuntimeLocalOverrides(candidate);
                _localConfig = candidate;
            }
            else if (!persist && effectiveLocal)
            {
                _runtimeLocalOverrides[id] = null;
                RemoveKey(_localConfig.Connections, id);
            }

            lock (_registryGate)
            {
                if (_registry.TryGetValue(id, out removed))
                    _registry.Remove(id);
                _connectionInfos.Remove(id);
                _health.TryRemove(id, out _);
            }
        }
        finally
        {
            _mutationGate.Release();
        }

        if (removed is not null)
            await removed.RetireAndDisposeAsync().ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>Registers a prebuilt custom backend for this runtime only.</summary>
    public Result RegisterBackend(string id, IStorageBackend backend, bool ownsBackend = true)
        => RegisterBackendAsync(id, backend, ownsBackend).ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>
    /// Registers a prebuilt custom backend after an asynchronous health check. Ownership transfers only
    /// when registration succeeds; replacement disposal completes before this method returns.
    /// </summary>
    public async Task<Result> RegisterBackendAsync(
        string id,
        IStorageBackend backend,
        bool ownsBackend = true,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionId(id);
        ArgumentNullException.ThrowIfNull(backend);
        var runtime = CaptureRuntimeSnapshot();
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(id, backend.ConnectionId, StringComparison.OrdinalIgnoreCase))
            return Result.Failure(StorageErrors.Conflict(
                $"Backend connection ID '{backend.ConnectionId}' does not match registry ID '{id}'."));

        var health = await CheckRegistrationHealthAsync(
            id,
            backend,
            runtime.HealthCheckTimeoutSeconds,
            runtime.Logger,
            cancellationToken).ConfigureAwait(false);
        if (health.IsFailure)
            return health;

        BackendEntry? previous = null;
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOperational();
            lock (_registryGate)
            {
                if (_registry.TryGetValue(id, out previous) && ReferenceEquals(previous.Backend, backend))
                    return Result.Success();
                var replacement = new BackendEntry(backend, ownsBackend);
                _registry[id] = replacement;
                _connectionInfos[id] = new StorageConnectionInfo(
                    backend.ConnectionId,
                    backend.Provider,
                    backend.Root,
                    Enabled: true);
            }
        }
        finally
        {
            _mutationGate.Release();
        }

        if (previous is not null)
            await previous.RetireAndDisposeAsync().ConfigureAwait(false);
        return Result.Success();
    }

    private static async Task<Result> CheckRegistrationHealthAsync(
        string id,
        IStorageBackend backend,
        int timeoutSeconds,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var result = await backend.CheckHealthAsync(linked.Token)
                .WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), cancellationToken)
                .ConfigureAwait(false);
            return result.IsSuccess
                ? Result.Success()
                : Result.Failure(StorageErrors.Unavailable(
                    $"Storage backend '{id}' failed its registration health check.",
                    result.Error?.Code ?? StorageErrors.ProviderErrorCode));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return Result.Failure(StorageErrors.Timeout(
                $"Storage backend '{id}' timed out during its registration health check."));
        }
        catch (TimeoutException)
        {
            timeout.Cancel();
            return Result.Failure(StorageErrors.Timeout(
                $"Storage backend '{id}' timed out during its registration health check."));
        }
        catch (Exception error)
        {
            logger.Error($"Storage backend '{id}' threw during its registration health check.", error);
            return Result.Failure(StorageErrors.ProviderError(
                $"Storage backend '{id}' failed its registration health check."));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        LifecycleState state;
        lock (_stateGate)
            state = _state;
        if (state == LifecycleState.Disposed)
            return;
        OnStopAsync().ConfigureAwait(false).GetAwaiter().GetResult();
        lock (_stateGate)
            _state = LifecycleState.Disposed;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        LifecycleState state;
        lock (_stateGate)
            state = _state;
        if (state == LifecycleState.Disposed)
            return;
        try
        {
            await OnStopAsync().ConfigureAwait(false);
        }
        finally
        {
            lock (_stateGate)
                _state = LifecycleState.Disposed;
            GC.SuppressFinalize(this);
        }
    }

    internal BackendEntry.BackendOperationLease AcquireOperation(string connectionId)
    {
        while (true)
        {
            EnsureOperational();
            BackendEntry entry;
            lock (_registryGate)
            {
                if (!_registry.TryGetValue(connectionId, out entry!))
                    throw new KeyNotFoundException($"Storage connection '{connectionId}' is not registered or enabled.");
            }
            if (entry.TryAcquire(out var lease))
                return lease!;
        }
    }

    private StorageEventPublisher? TryCaptureEventPublisher()
    {
        lock (_stateGate)
            return _context is { } context ? new StorageEventPublisher(context.Events, context.Logger) : null;
    }

    private ILogger? TryCaptureLogger()
    {
        lock (_stateGate)
            return _context?.Logger;
    }

    internal StorageConnectionObserver ConnectionObserver => _connectionObserver;

    /// <summary>Gets whether the library keeps its configuration in memory only.</summary>
    public bool RuntimeOnly => _runtimeOnly;

    /// <summary>Reads a configuration section, from memory in runtime-only mode.</summary>
    private T ReadConfig<T>(LibraryContext context) where T : ConfigModelBase, new() =>
        _runtimeOnly
            ? (T)_memoryConfig.GetOrAdd(typeof(T), _ => typeof(T) == typeof(StorageConfig) && _runtimeSettings is not null ? _runtimeSettings : new T())
            : context.Configuration.Get<T>();

    /// <summary>Saves a configuration section, to memory in runtime-only mode.</summary>
    /// <remarks>
    /// Marks the save as the library's own, so the resulting config-change notification is not
    /// applied a second time (see <see cref="OnConfigChangedAsync"/>): the callers hold the mutation
    /// gate and update the registry themselves.
    /// </remarks>
    private async Task SaveConfigAsync<T>(LibraryContext context, T value) where T : ConfigModelBase, new()
    {
        if (_runtimeOnly)
        {
            _memoryConfig[typeof(T)] = value;
            return;
        }

        _savingOwnConfig.Value = true;
        try
        {
            await context.Configuration.SaveAsync(value).ConfigureAwait(false);
        }
        finally
        {
            _savingOwnConfig.Value = false;
        }
    }

    internal StorageEventPublisher CaptureEventPublisher()
    {
        LibraryContext context;
        lock (_stateGate)
            context = _context ?? throw new InvalidOperationException("Storage library context is unavailable.");
        return new StorageEventPublisher(context.Events, context.Logger);
    }

    private static async Task ObserveProbeAndDisposeReplacementAsync(
        string id,
        BackendEntry replacement,
        Task<Result>? probeTask,
        ILogger logger)
    {
        if (probeTask is not null)
        {
            try { await probeTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                TryLog(logger, $"Unpublished storage replacement '{id}' health probe failed after the caller stopped waiting.", error);
            }
        }

        try { await replacement.RetireAndDisposeAsync().ConfigureAwait(false); }
        catch (Exception error)
        {
            TryLog(logger, $"Failed to dispose unpublished storage replacement '{id}'.", error);
        }
    }

    private static async Task<HealthProbe> ProbeHealthAsync(
        HealthTarget target,
        int timeoutSeconds,
        ILogger logger)
    {
        Task<Result>? probeTask = null;
        var provider = target.Lease.Backend.Provider;
        var started = Stopwatch.GetTimestamp();
        HealthProbe Probe(bool healthy, string detail) =>
            new(target.Id, healthy, detail) { Provider = provider, Latency = Stopwatch.GetElapsedTime(started) };
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
        {
            try
            {
                probeTask = target.Lease.Backend.CheckHealthAsync(timeout.Token);
                var result = await probeTask
                    .WaitAsync(TimeSpan.FromSeconds(timeoutSeconds))
                    .ConfigureAwait(false);
                return result.IsSuccess
                    ? Probe(true, string.Empty)
                    : Probe(false, result.Error?.Code ?? "storage.provider_error");
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                return Probe(false, "storage.timeout");
            }
            catch (TimeoutException)
            {
                timeout.Cancel();
                return Probe(false, "storage.timeout");
            }
            catch (Exception)
            {
                return Probe(false, "storage.provider_error");
            }
            finally
            {
                if (probeTask is null)
                {
                    target.Lease.Dispose();
                }
                else
                {
                    var release = ObserveProbeAndReleaseHealthLeaseAsync(target, probeTask, logger);
                    if (probeTask.IsCompleted)
                        await release.ConfigureAwait(false);
                    else
                        _ = release;
                }
            }
        }
    }

    private static async Task ObserveProbeAndReleaseHealthLeaseAsync(
        HealthTarget target,
        Task<Result> probeTask,
        ILogger logger)
    {
        try { await probeTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            TryLog(logger, $"Storage connection '{target.Id}' health probe failed after the caller stopped waiting.", error);
        }
        finally
        {
            target.Lease.Dispose();
        }
    }

    private static void TryLog(ILogger logger, string message, Exception error)
    {
        try { logger.Error(message, error); }
        catch { }
    }

    private LifecycleState GetLifecycleState()
    {
        lock (_stateGate)
            return _state;
    }

    private string GetDefaultConnectionId()
    {
        var runtime = CaptureRuntimeSnapshot();
        _defaultConnectionSnapshotCaptured?.Invoke();
        return runtime.DefaultConnection;
    }

    private RuntimeSnapshot CaptureRuntimeSnapshot()
    {
        if (TryCaptureRuntimeSnapshot(out var runtime, out var state))
            return runtime;
        throw new InvalidOperationException($"Storage library is {DescribeState(state)}; public access requires an initialized library.");
    }

    private bool TryCaptureRuntimeSnapshot(out RuntimeSnapshot runtime, out LifecycleState state)
    {
        lock (_stateGate)
        {
            state = _state;
            if (state is not (LifecycleState.Initialized or LifecycleState.Started))
            {
                runtime = default;
                return false;
            }

            var context = _context ?? throw new InvalidOperationException(
                "Storage library runtime context is unavailable while the library is initialized.");
            var storageConfig = _storageConfig ?? throw new InvalidOperationException(
                "Storage library runtime configuration is unavailable while the library is initialized.");
            runtime = new RuntimeSnapshot(
                context,
                storageConfig,
                context.Logger,
                _enabled,
                storageConfig.DefaultConnection,
                storageConfig.MaxBufferedDownloadBytes,
                storageConfig.HealthCheckTimeoutSeconds);
            return true;
        }
    }

    private void EnsureOperational() => _ = CaptureRuntimeSnapshot();

    private static string DescribeState(LifecycleState state) => state switch
    {
        LifecycleState.Created => "not initialized",
        LifecycleState.Configured => "configured but not initialized",
        LifecycleState.Stopping => "stopping",
        LifecycleState.Stopped => "stopped",
        LifecycleState.Disposed => "disposed",
        _ => state.ToString().ToLowerInvariant()
    };

    private static void EnsureValid(string section, CodeLogic.Core.Configuration.ConfigValidationResult validation)
    {
        if (!validation.IsValid)
            throw new InvalidOperationException($"Storage configuration section '{section}' is invalid: {string.Join("; ", validation.Errors)}");
    }

    private static StorageConnectionConfigBase CloneProviderConnection(StorageConnectionConfigBase source) =>
        (StorageConnectionConfigBase)JsonSerializer.Deserialize(
            JsonSerializer.Serialize(source, source.GetType()),
            source.GetType())!;

    private static ProviderDescriptor? DescribeProviderConnection(Type connectionType)
    {
        if (connectionType == typeof(S3ConnectionConfig))
            return new ProviderDescriptor(StorageProvider.S3, "storage.s3");
        if (connectionType == typeof(FtpConnectionConfig))
            return new ProviderDescriptor(StorageProvider.Ftp, "storage.ftp");
        if (connectionType == typeof(SftpConnectionConfig))
            return new ProviderDescriptor(StorageProvider.Sftp, "storage.sftp");
        if (connectionType == typeof(WebDavConnectionConfig))
            return new ProviderDescriptor(StorageProvider.WebDav, "storage.webdav");
        if (connectionType == typeof(AzureBlobConnectionConfig))
            return new ProviderDescriptor(StorageProvider.AzureBlob, "storage.azure");
        if (connectionType == typeof(GoogleCloudConnectionConfig))
            return new ProviderDescriptor(StorageProvider.GoogleCloudStorage, "storage.gcs");
        if (connectionType == typeof(SwiftConnectionConfig))
            return new ProviderDescriptor(StorageProvider.OpenStackSwift, "storage.swift");
        return null;
    }

    private ProviderStorageConfigBase GetProviderConfig(LibraryContext context, Type connectionType)
    {
        if (connectionType == typeof(S3ConnectionConfig)) return ReadConfig<S3StorageConfig>(context);
        if (connectionType == typeof(FtpConnectionConfig)) return ReadConfig<FtpStorageConfig>(context);
        if (connectionType == typeof(SftpConnectionConfig)) return ReadConfig<SftpStorageConfig>(context);
        if (connectionType == typeof(WebDavConnectionConfig)) return ReadConfig<WebDavStorageConfig>(context);
        if (connectionType == typeof(AzureBlobConnectionConfig)) return ReadConfig<AzureStorageConfig>(context);
        if (connectionType == typeof(GoogleCloudConnectionConfig)) return ReadConfig<GoogleCloudStorageConfig>(context);
        if (connectionType == typeof(SwiftConnectionConfig)) return ReadConfig<SwiftStorageConfig>(context);
        throw new NotSupportedException($"Provider connection type '{connectionType.FullName}' is not supported.");
    }

    private Task SaveProviderConfigAsync(LibraryContext context, ProviderStorageConfigBase config) => config switch
    {
        S3StorageConfig value => SaveConfigAsync(context, value),
        FtpStorageConfig value => SaveConfigAsync(context, value),
        SftpStorageConfig value => SaveConfigAsync(context, value),
        WebDavStorageConfig value => SaveConfigAsync(context, value),
        AzureStorageConfig value => SaveConfigAsync(context, value),
        GoogleCloudStorageConfig value => SaveConfigAsync(context, value),
        SwiftStorageConfig value => SaveConfigAsync(context, value),
        _ => throw new NotSupportedException($"Provider configuration type '{config.GetType().FullName}' is not supported.")
    };

    private string? FindConfiguredSection(
        string id,
        LibraryContext context,
        Type? exceptConnectionType = null)
    {
        if (exceptConnectionType != typeof(LocalConnectionConfig) &&
            TryFindKey(ReadConfig<LocalStorageConfig>(context).Connections, id, out _))
            return "storage.local";
        if (exceptConnectionType != typeof(S3ConnectionConfig) && ReadConfig<S3StorageConfig>(context).ContainsConnection(id))
            return "storage.s3";
        if (exceptConnectionType != typeof(FtpConnectionConfig) && ReadConfig<FtpStorageConfig>(context).ContainsConnection(id))
            return "storage.ftp";
        if (exceptConnectionType != typeof(SftpConnectionConfig) && ReadConfig<SftpStorageConfig>(context).ContainsConnection(id))
            return "storage.sftp";
        if (exceptConnectionType != typeof(WebDavConnectionConfig) && ReadConfig<WebDavStorageConfig>(context).ContainsConnection(id))
            return "storage.webdav";
        if (exceptConnectionType != typeof(AzureBlobConnectionConfig) && ReadConfig<AzureStorageConfig>(context).ContainsConnection(id))
            return "storage.azure";
        if (exceptConnectionType != typeof(GoogleCloudConnectionConfig) && ReadConfig<GoogleCloudStorageConfig>(context).ContainsConnection(id))
            return "storage.gcs";
        if (exceptConnectionType != typeof(SwiftConnectionConfig) && ReadConfig<SwiftStorageConfig>(context).ContainsConnection(id))
            return "storage.swift";
        return null;
    }

    private ProviderConfigMatch? FindProviderConfigContaining(string id, LibraryContext context)
    {
        ProviderStorageConfigBase[] configs =
        [
            ReadConfig<S3StorageConfig>(context),
            ReadConfig<FtpStorageConfig>(context),
            ReadConfig<SftpStorageConfig>(context),
            ReadConfig<WebDavStorageConfig>(context),
            ReadConfig<AzureStorageConfig>(context),
            ReadConfig<GoogleCloudStorageConfig>(context),
            ReadConfig<SwiftStorageConfig>(context)
        ];
        foreach (var config in configs)
        {
            if (!config.ContainsConnection(id)) continue;
            var connectionType = config.EnumerateConnections().First().Value.GetType();
            return new ProviderConfigMatch(config, DescribeProviderConnection(connectionType)!.Value);
        }
        return null;
    }

    private static void ValidateGlobalIds(
        LocalStorageConfig local,
        S3StorageConfig s3,
        FtpStorageConfig ftp,
        SftpStorageConfig sftp,
        WebDavStorageConfig webDav,
        AzureStorageConfig azure,
        GoogleCloudStorageConfig gcs,
        SwiftStorageConfig swift)
    {
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Add(local.Connections.Keys, "storage.local");
        Add(s3.Connections.Keys, "storage.s3");
        Add(ftp.Connections.Keys, "storage.ftp");
        Add(sftp.Connections.Keys, "storage.sftp");
        Add(webDav.Connections.Keys, "storage.webdav");
        Add(azure.Connections.Keys, "storage.azure");
        Add(gcs.Connections.Keys, "storage.gcs");
        Add(swift.Connections.Keys, "storage.swift");
        return;

        void Add(IEnumerable<string> connectionIds, string section)
        {
            foreach (var id in connectionIds)
            {
                if (ids.TryGetValue(id, out var existing))
                    throw new InvalidOperationException(
                        $"Duplicate storage connection ID '{id}' appears in '{existing}' and '{section}'. IDs are case-insensitive.");
                ids.Add(id, section);
            }
        }
    }

    private void AddProviderConnections<TConnection>(
        IDictionary<string, BackendEntry> entries,
        IDictionary<string, StorageConnectionInfo> infos,
        ProviderStorageConfigBase<TConnection> config,
        StorageProvider provider,
        long maxBufferedDownloadBytes,
        TransferLimits libraryLimits)
        where TConnection : StorageConnectionConfigBase
    {
        _factories.TryGetValue(typeof(TConnection), out var factory);
        foreach (var (id, connection) in config.Connections)
        {
            infos.Add(id, DescribeConnection(id, provider, connection));
            if (!connection.Enabled)
                continue;
            if (factory is null)
                throw new InvalidOperationException($"The {provider} provider factory is not registered.");
            entries.Add(id, new BackendEntry(
                WithLimits(factory.Create(id, connection, maxBufferedDownloadBytes, _connectionObserver), connection, libraryLimits),
                ownsBackend: true));
        }
    }

    private static StorageConnectionInfo DescribeConnection(string id, StorageProvider provider, StorageConnectionConfigBase connection)
    {
        var (host, port, security) = StorageEndpoints.Describe(connection);
        return new StorageConnectionInfo(id, provider, connection.MountRoot, connection.Enabled)
        {
            Host = host,
            Port = port,
            Security = security
        };
    }

    /// <summary>Attaches a connection's speed limits, plus the library-wide totals, to its backend.</summary>
    private static IStorageBackend WithLimits(IStorageBackend backend, StorageConnectionConfigBase configuration, TransferLimits libraryLimits)
    {
        // Pooled connections also record their session limit, so a relay that needs two sessions on one
        // connection fails at once instead of waiting for a session that never frees.
        var session = configuration switch
        {
            FtpConnectionConfig ftp => ftp.Session,
            SftpConnectionConfig sftp => sftp.Session,
            _ => null
        };
        if (session is not null)
            StorageTransferPipeline.SetSessionLimit(backend, session.MaxSessions);
        return WithLimits(backend, configuration.TransferLimits, libraryLimits);
    }

    private static IStorageBackend WithLimits(IStorageBackend backend, LocalConnectionConfig configuration, TransferLimits libraryLimits) =>
        WithLimits(backend, configuration.TransferLimits, libraryLimits);

    private static IStorageBackend WithLimits(IStorageBackend backend, StorageTransferLimitsConfig? configured, TransferLimits libraryLimits)
    {
        var limits = configured ?? new StorageTransferLimitsConfig();
        StorageTransferPipeline.SetLimits(backend, limits.MaxUploadBytesPerSecond, limits.MaxDownloadBytesPerSecond, libraryLimits);
        return backend;
    }

    private static void ValidateConnectionId(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("A storage connection ID is required.", nameof(connectionId));
    }

    private string? FindConfiguredNonLocalSection(string id, LibraryContext context)
    {
        if (Contains(ReadConfig<S3StorageConfig>(context), id)) return "storage.s3";
        if (Contains(ReadConfig<FtpStorageConfig>(context), id)) return "storage.ftp";
        if (Contains(ReadConfig<SftpStorageConfig>(context), id)) return "storage.sftp";
        if (Contains(ReadConfig<WebDavStorageConfig>(context), id)) return "storage.webdav";
        if (Contains(ReadConfig<AzureStorageConfig>(context), id)) return "storage.azure";
        if (Contains(ReadConfig<GoogleCloudStorageConfig>(context), id)) return "storage.gcs";
        if (Contains(ReadConfig<SwiftStorageConfig>(context), id)) return "storage.swift";
        return null;

        static bool Contains(ProviderStorageConfigBase providerConfig, string connectionId) =>
            providerConfig.ContainsConnection(connectionId);
    }

    private static StorageConfig CloneStorageConfig(StorageConfig source) => new()
    {
        Enabled = source.Enabled,
        DefaultConnection = source.DefaultConnection,
        HealthCheckTimeoutSeconds = source.HealthCheckTimeoutSeconds,
        MaxBufferedDownloadBytes = source.MaxBufferedDownloadBytes,
        MaxTotalUploadBytesPerSecond = source.MaxTotalUploadBytesPerSecond,
        MaxTotalDownloadBytesPerSecond = source.MaxTotalDownloadBytesPerSecond
    };

    private static LocalStorageConfig CloneLocalConfig(LocalStorageConfig source)
    {
        var clone = new LocalStorageConfig();
        foreach (var (id, connection) in source.Connections)
            clone.Connections[id] = Clone(connection);
        return clone;
    }

    private static LocalConnectionConfig Clone(LocalConnectionConfig source) => new()
    {
        Enabled = source.Enabled,
        RootPath = source.RootPath,
        FollowLinks = source.FollowLinks,
        TransferLimits = new StorageTransferLimitsConfig
        {
            MaxUploadBytesPerSecond = source.TransferLimits?.MaxUploadBytesPerSecond,
            MaxDownloadBytesPerSecond = source.TransferLimits?.MaxDownloadBytesPerSecond
        }
    };

    private void ApplyRuntimeLocalOverrides(LocalStorageConfig target)
    {
        foreach (var (id, connection) in _runtimeLocalOverrides)
        {
            RemoveKey(target.Connections, id);
            if (connection is not null)
                target.Connections[id] = Clone(connection);
        }
    }

    private static void SetLocalConnection(LocalStorageConfig target, string id, LocalConnectionConfig connection)
    {
        RemoveKey(target.Connections, id);
        target.Connections[id] = Clone(connection);
    }

    private static void RemoveKey<T>(IDictionary<string, T> dictionary, string id)
    {
        if (TryFindKey(dictionary, id, out var key))
            dictionary.Remove(key!);
    }

    private static bool TryFindKey<T>(IDictionary<string, T> dictionary, string id, out string? key)
    {
        key = dictionary.Keys.FirstOrDefault(candidate => string.Equals(candidate, id, StringComparison.OrdinalIgnoreCase));
        return key is not null;
    }

    private sealed record HealthProbe(string Id, bool Healthy, string Detail)
    {
        public StorageProvider Provider { get; init; }
        public TimeSpan Latency { get; init; }
    }
    private sealed record HealthTarget(string Id, BackendEntry.BackendOperationLease Lease);
    private readonly record struct ProviderDescriptor(StorageProvider Provider, string Section);
    private sealed record ProviderConfigMatch(ProviderStorageConfigBase Config, ProviderDescriptor Descriptor);
    private readonly record struct RuntimeSnapshot(
        LibraryContext Context,
        StorageConfig StorageConfig,
        ILogger Logger,
        bool Enabled,
        string DefaultConnection,
        long MaxBufferedDownloadBytes,
        int HealthCheckTimeoutSeconds);

    private enum LifecycleState
    {
        Created,
        Configured,
        Initialized,
        Started,
        Stopping,
        Stopped,
        Disposed
    }
}
