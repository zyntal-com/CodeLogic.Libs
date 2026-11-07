using System.Buffers;
using System.IO.Pipelines;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Providers;
using CodeLogic.Core.Results;

namespace CL.Storage.Registry;

/// <summary>Relays storage transfers with bounded buffering and cleanup-safe staging objects.</summary>
internal static partial class StorageTransferCoordinator
{
    internal const long PauseWriterThreshold = 1_048_576;
    internal const long ResumeWriterThreshold = 524_288;
    internal const int SegmentSize = 65_536;
    private const int PageSize = 1_000;
    private const int StagingNameAttempts = 8;

    /// <summary>How often a Rename transfer tries the next free name when its chosen one is taken at commit.</summary>
    private const int RenameRetries = 8;
}
