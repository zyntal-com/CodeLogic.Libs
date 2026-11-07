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
    private readonly Lock _stateGate = new();
    private readonly Lock _registryGate = new();
    // Transfer queues opened from this library; stopping the library stops them first (B43).
    private readonly List<Queue.StorageTransferQueue> _queues = [];
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly Dictionary<string, BackendEntry> _registry = [with(StringComparer.OrdinalIgnoreCase)];
    private readonly Dictionary<string, StorageServiceProxy> _proxies = [with(StringComparer.OrdinalIgnoreCase)];
    private readonly Dictionary<string, StorageConnectionInfo> _connectionInfos = [with(StringComparer.OrdinalIgnoreCase)];
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
    private readonly Dictionary<string, LocalConnectionConfig?> _runtimeLocalOverrides = [with(StringComparer.OrdinalIgnoreCase)];
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

    private const long DefaultTestBufferBytes = 1_048_576;

    /// <summary>How often a Rename tries the next free name when its chosen one is taken at the server's rename.</summary>
    private const int RenameRetries = 8;
}
