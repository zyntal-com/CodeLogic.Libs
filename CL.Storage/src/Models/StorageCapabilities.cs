namespace CL.Storage.Models;

/// <summary>Granular, capability-gated behavior exposed by a storage connection.</summary>
[Flags]
public enum StorageFeature : ulong
{
    /// <summary>No optional storage behavior is advertised.</summary>
    None = 0,
    /// <summary>The provider stores concrete directory entries.</summary>
    PhysicalDirectories = 1UL << 0,
    /// <summary>The provider derives directories from object-key prefixes.</summary>
    VirtualDirectories = 1UL << 1,
    /// <summary>The provider can represent symbolic links or equivalent references.</summary>
    Links = 1UL << 2,
    /// <summary>Files can be copied through the connection API.</summary>
    FileCopy = 1UL << 3,
    /// <summary>Directory trees can be copied through the connection API.</summary>
    DirectoryCopy = 1UL << 4,
    /// <summary>Files can be moved through the connection API.</summary>
    FileMove = 1UL << 5,
    /// <summary>Directory trees can be moved through the connection API.</summary>
    DirectoryMove = 1UL << 6,
    /// <summary>File content can be copied without downloading it through the caller.</summary>
    ServerSideCopy = 1UL << 7,
    /// <summary>A provider-native rename or copy/delete move is available.</summary>
    ServerSideMove = 1UL << 8,
    /// <summary>Copying is implemented by a bounded client-side stream relay.</summary>
    RelayedCopy = 1UL << 9,
    /// <summary>Native moves commit the destination and remove the source atomically.</summary>
    AtomicMove = 1UL << 10,
    /// <summary>Overwrite uploads can replace an existing file without exposing partial content.</summary>
    AtomicReplace = 1UL << 11,
    /// <summary>Downloads can request a byte offset and optional length.</summary>
    RangeReads = 1UL << 12,
    /// <summary>User metadata can be read through <see cref="Abstractions.IStorageMetadataService"/>.</summary>
    MetadataRead = 1UL << 13,
    /// <summary>User metadata can be updated through <see cref="Abstractions.IStorageMetadataService"/>.</summary>
    MetadataWrite = 1UL << 14,
    /// <summary>Create-only uploads are enforced atomically.</summary>
    ConditionalCreate = 1UL << 15,
    /// <summary>Uploads can require a matching ETag or provider version.</summary>
    ConditionalUpdate = 1UL << 16,
    /// <summary>Deletes can require a matching ETag or provider version.</summary>
    ConditionalDelete = 1UL << 17,
    /// <summary>The provider returns opaque continuation tokens instead of preloading all results.</summary>
    ServerPagination = 1UL << 18,
    /// <summary>The provider can report stored checksums through <see cref="Abstractions.IStorageChecksumService"/>.</summary>
    Checksums = 1UL << 19,
    /// <summary>Large uploads are split into bounded provider multipart requests.</summary>
    MultipartUpload = 1UL << 20,
    /// <summary>
    /// Reserved for a provider-native resumable upload session. No built-in provider reports it: the library resumes
    /// staged writes through <see cref="Append"/> (<c>ConflictPolicy.Resume</c>), so check that flag instead.
    /// </summary>
    ResumableUpload = 1UL << 21,
    /// <summary>Temporary signed read URLs can be generated.</summary>
    SignedReadUrls = 1UL << 22,
    /// <summary>Temporary signed write URLs can be generated.</summary>
    SignedWriteUrls = 1UL << 23,
    /// <summary>Exact object versions can be read and managed.</summary>
    Versioning = 1UL << 24,
    /// <summary>Object tags can be read and updated.</summary>
    Tags = 1UL << 25,
    /// <summary>Provider-native access-control lists are available through native access.</summary>
    AccessControlLists = 1UL << 26,
    /// <summary>Provider-native object or blob leases are available.</summary>
    Leases = 1UL << 27,
    /// <summary>The provider can append content without replacing the complete object.</summary>
    Append = 1UL << 28,
    /// <summary>The provider can emit object-change notifications.</summary>
    ChangeNotifications = 1UL << 29,
    /// <summary>Unix permission bits can be changed through <see cref="Abstractions.IStorageAttributeService"/>.</summary>
    Permissions = 1UL << 30,
    /// <summary>Numeric owner and group can be changed through <see cref="Abstractions.IStorageAttributeService"/>.</summary>
    Ownership = 1UL << 31,
    /// <summary>Modification (and possibly access) times can be set through <see cref="Abstractions.IStorageAttributeService"/>.</summary>
    SetTimestamps = 1UL << 32,
    /// <summary>Symbolic links can be created through <see cref="Abstractions.IStorageAttributeService"/>.</summary>
    CreateLinks = 1UL << 33,
    /// <summary>Link targets can be read through <see cref="Abstractions.IStorageAttributeService"/>.</summary>
    ReadLinks = 1UL << 34,
    /// <summary>Raw protocol commands can be sent through <see cref="Abstractions.IStorageCommandService"/>.</summary>
    RawCommands = 1UL << 35,
    /// <summary>Free and used space can be read through <see cref="Abstractions.IStorageSpaceService"/>.</summary>
    SpaceInfo = 1UL << 36,
    /// <summary>
    /// Names that differ only by letter case are the same item (local folders on Windows and macOS). Sync
    /// refuses to copy two such names into this connection instead of letting the last copy win.
    /// </summary>
    CaseInsensitivePaths = 1UL << 37
}

/// <summary>Optional provider limits. A null value means the provider did not expose a reliable limit.</summary>
public sealed record StorageLimits
{
    /// <summary>Gets the largest accepted or provider-supported page size.</summary>
    public int? MaxPageSize { get; init; }
    /// <summary>Gets the maximum complete object size in bytes.</summary>
    public long? MaxObjectBytes { get; init; }
    /// <summary>Gets the maximum size of one non-multipart upload request in bytes.</summary>
    public long? MaxSingleUploadBytes { get; init; }
    /// <summary>Gets the maximum encoded user-metadata size in bytes.</summary>
    public int? MaxMetadataBytes { get; init; }
    /// <summary>Gets the maximum number of portable tags attached to one object.</summary>
    public int? MaxTags { get; init; }
    /// <summary>Gets the maximum number of inputs accepted by a batch helper.</summary>
    public int? MaxBatchItems { get; init; }
    /// <summary>Gets the provider-preferred multipart or resumable upload chunk size in bytes.</summary>
    public int? PreferredUploadPartBytes { get; init; }
}

/// <summary>
/// Describes behavior implemented by a storage connection. The six legacy convenience properties are
/// retained as projections while callers migrate to <see cref="Features"/>.
/// </summary>
public sealed record StorageCapabilities
{
    /// <summary>Initializes an immutable capability snapshot.</summary>
    /// <param name="features">Granular behavior implemented by the connection.</param>
    /// <param name="limits">Optional provider limits, or <see langword="null"/> when unknown.</param>
    public StorageCapabilities(StorageFeature features, StorageLimits? limits = null)
    {
        Features = features;
        Limits = limits ?? new StorageLimits();
    }

    /// <summary>Compatibility constructor for the original six capability booleans.</summary>
    /// <param name="Directories">Whether the provider supports directories.</param>
    /// <param name="NativeCopy">Whether file copying stays within the provider.</param>
    /// <param name="NativeMove">Whether native file and directory moves are available.</param>
    /// <param name="RangeReads">Whether byte-range downloads are supported.</param>
    /// <param name="Metadata">Whether user metadata can be read.</param>
    /// <param name="ServerPagination">Whether listings use provider continuation tokens.</param>
    public StorageCapabilities(
        bool Directories,
        bool NativeCopy,
        bool NativeMove,
        bool RangeReads,
        bool Metadata,
        bool ServerPagination)
        : this(
            (Directories ? StorageFeature.PhysicalDirectories : StorageFeature.None) |
            (NativeCopy ? StorageFeature.FileCopy | StorageFeature.ServerSideCopy : StorageFeature.None) |
            (NativeMove
                ? StorageFeature.FileMove | StorageFeature.DirectoryMove | StorageFeature.ServerSideMove
                : StorageFeature.None) |
            (RangeReads ? StorageFeature.RangeReads : StorageFeature.None) |
            (Metadata ? StorageFeature.MetadataRead : StorageFeature.None) |
            (ServerPagination ? StorageFeature.ServerPagination : StorageFeature.None))
    {
    }

    /// <summary>Gets the complete granular feature set.</summary>
    public StorageFeature Features { get; }
    /// <summary>Gets known provider bounds. Null properties represent unknown bounds.</summary>
    public StorageLimits Limits { get; }

    /// <summary>Gets whether either physical or virtual directories are supported.</summary>
    public bool Directories => SupportsAny(
        StorageFeature.PhysicalDirectories | StorageFeature.VirtualDirectories);
    /// <summary>Gets whether copying can stay within the provider.</summary>
    public bool NativeCopy => Supports(StorageFeature.ServerSideCopy);
    /// <summary>Gets whether a provider-native move is available.</summary>
    public bool NativeMove => Supports(StorageFeature.ServerSideMove);
    /// <summary>Gets whether byte-range downloads are supported.</summary>
    public bool RangeReads => Supports(StorageFeature.RangeReads);
    /// <summary>Gets whether user metadata can be read.</summary>
    public bool Metadata => Supports(StorageFeature.MetadataRead);
    /// <summary>Gets whether listings use provider continuation tokens.</summary>
    public bool ServerPagination => Supports(StorageFeature.ServerPagination);

    /// <summary>Determines whether every requested feature flag is present.</summary>
    /// <param name="feature">One or more required flags.</param>
    /// <returns><see langword="true"/> when all requested flags are present.</returns>
    public bool Supports(StorageFeature feature) => (Features & feature) == feature;
    /// <summary>Determines whether at least one requested feature flag is present.</summary>
    /// <param name="features">One or more alternative flags.</param>
    /// <returns><see langword="true"/> when any requested flag is present.</returns>
    public bool SupportsAny(StorageFeature features) => (Features & features) != 0;

    /// <summary>Deconstructs the original six compatibility capability values.</summary>
    /// <param name="directories">Receives <see cref="Directories"/>.</param>
    /// <param name="nativeCopy">Receives <see cref="NativeCopy"/>.</param>
    /// <param name="nativeMove">Receives <see cref="NativeMove"/>.</param>
    /// <param name="rangeReads">Receives <see cref="RangeReads"/>.</param>
    /// <param name="metadata">Receives <see cref="Metadata"/>.</param>
    /// <param name="serverPagination">Receives <see cref="ServerPagination"/>.</param>
    public void Deconstruct(
        out bool directories,
        out bool nativeCopy,
        out bool nativeMove,
        out bool rangeReads,
        out bool metadata,
        out bool serverPagination)
    {
        directories = Directories;
        nativeCopy = NativeCopy;
        nativeMove = NativeMove;
        rangeReads = RangeReads;
        metadata = Metadata;
        serverPagination = ServerPagination;
    }
}
