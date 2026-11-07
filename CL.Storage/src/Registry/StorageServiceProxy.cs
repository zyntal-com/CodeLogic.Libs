using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Events;
using CL.Storage.Models;
using CodeLogic.Core.Events;
using CodeLogic.Core.Results;
using System.Runtime.CompilerServices;

namespace CL.Storage.Registry;

internal sealed partial class StorageServiceProxy :
    IStorageService,
    IStorageMetadataService,
    IStorageTagService,
    IStorageSignedUrlService,
    IStorageVersionService,
    IStorageAttributeService,
    IStorageChecksumService,
    IStorageAppendService,
    IStorageCommandService,
    IStorageSpaceService,
    Sync.IStorageWatchService,
    IStorageConditionEnforcementSource
{
    private readonly StorageLibrary _library;
    private readonly string _connectionId;

    public StorageServiceProxy(StorageLibrary library, string connectionId)
    {
        _library = library;
        _connectionId = connectionId;
    }
}
