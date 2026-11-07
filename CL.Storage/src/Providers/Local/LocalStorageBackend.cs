using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CodeLogic.Core.Results;

namespace CL.Storage.Providers.Local;

/// <summary>Provides storage operations over a local path or mounted UNC root.</summary>
public sealed partial class LocalStorageBackend : IStorageBackend, IStorageAttributeService, IStorageAppendService, IStorageSpaceService, Sync.IStorageWatchService
{
    /// <inheritdoc />
    public const long DefaultMaxBufferedDownloadBytes = 67_108_864;
    private static readonly StorageCapabilities LocalCapabilities = new(
        StorageFeature.PhysicalDirectories |
        StorageFeature.FileCopy |
        StorageFeature.FileMove |
        StorageFeature.DirectoryMove |
        StorageFeature.ServerSideCopy |
        StorageFeature.ServerSideMove |
        StorageFeature.AtomicMove |
        StorageFeature.AtomicReplace |
        StorageFeature.ConditionalCreate |
        StorageFeature.RangeReads |
        StorageFeature.ChangeNotifications |
        StorageFeature.SpaceInfo |
        StorageFeature.Append |
        StorageFeature.Links |
        StorageFeature.SetTimestamps |
        StorageFeature.CreateLinks |
        StorageFeature.ReadLinks |
        // Unix permission bits exist only on Unix-like systems.
        (OperatingSystem.IsWindows() ? StorageFeature.None : StorageFeature.Permissions));

    private readonly LocalPathResolver _paths;
    private readonly long _maxBufferedDownloadBytes;
    private readonly StorageCapabilities _capabilities;

    /// <summary>
    /// Initializes a root-confined local-filesystem backend. Path checks protect against traversal in
    /// caller-supplied paths; they are not a sandbox against concurrent changes to the tree under the root.
    /// </summary>
    /// <param name="connectionId">Unique connection ID exposed by the storage registry.</param>
    /// <param name="configuration">Local root and link-handling configuration.</param>
    /// <param name="maxBufferedDownloadBytes">Maximum size accepted by buffered download helpers.</param>
    public LocalStorageBackend(
        string connectionId,
        LocalConnectionConfig configuration,
        long maxBufferedDownloadBytes = DefaultMaxBufferedDownloadBytes)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("A connection ID is required.", nameof(connectionId));
        ArgumentNullException.ThrowIfNull(configuration);
        if (maxBufferedDownloadBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBufferedDownloadBytes));

        ConnectionId = connectionId;
        // The probed answer drives both the advertised capability and every path comparison the resolver makes.
        var root = LocalPathResolver.NormalizeRoot(configuration.RootPath);
        var caseInsensitive = DetectCaseInsensitive(root);
        _paths = new LocalPathResolver(root, configuration.FollowLinks, caseInsensitive);
        _maxBufferedDownloadBytes = maxBufferedDownloadBytes;
        _capabilities = caseInsensitive
            ? new StorageCapabilities(LocalCapabilities.Features | StorageFeature.CaseInsensitivePaths, LocalCapabilities.Limits)
            : LocalCapabilities;
    }
}
