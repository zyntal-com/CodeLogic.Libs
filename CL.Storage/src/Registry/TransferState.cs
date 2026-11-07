using System.Buffers;
using System.IO.Pipelines;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Providers;
using CodeLogic.Core.Results;

namespace CL.Storage.Registry;

/// <summary>What a transfer did before it stopped, so a failure can be reported precisely.</summary>
internal sealed class TransferState
{
    /// <summary>The source item as it was when the transfer read it.</summary>
    public StorageItem? Source { get; set; }
    /// <summary>A staging object left behind (kept for resume, or cleanup failed).</summary>
    public string? StagingLeftBehind { get; set; }
    /// <summary>Whether the staging object was kept on purpose so the transfer can resume.</summary>
    public bool StagingResumable { get; set; }
    /// <summary>Bytes in the kept staging object.</summary>
    public long BytesStaged { get; set; }
    /// <summary>Whether content reached its destination path.</summary>
    public bool DestinationCommitted { get; set; }
    /// <summary>Whether a replaced destination was restored after a failure.</summary>
    public bool? BackupRestored { get; set; }
    /// <summary>A backup that could not be removed.</summary>
    public string? BackupLeftBehind { get; set; }
    /// <summary>Whether rolling back the destination did not finish.</summary>
    public bool RollbackIncomplete { get; set; }
    /// <summary>How a destination condition was enforced, even when the promote was refused.</summary>
    public StorageConditionEnforcement ConditionEnforcement { get; set; }
    /// <summary>Files committed at the destination so far (a directory transfer that stopped part-way).</summary>
    public long FilesCommitted { get; set; }
    /// <summary>Bytes of the files committed so far.</summary>
    public long BytesCommitted { get; set; }
    /// <summary>Directories created at the destination so far.</summary>
    public long DirectoriesCreated { get; set; }
    /// <summary>Source items a move already deleted, when it stopped before deleting them all.</summary>
    public long SourceItemsDeleted { get; set; }
}
