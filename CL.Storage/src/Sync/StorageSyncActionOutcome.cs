using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Sync;

/// <summary>How one planned step turned out.</summary>
public enum StorageSyncActionOutcome
{
    /// <summary>Not run: a dry run, a blocked conflict, or the run stopped first.</summary>
    NotRun = 0,
    /// <summary>Done.</summary>
    Applied = 1,
    /// <summary>Failed; see the error.</summary>
    Failed = 2,
    /// <summary>The item changed after the plan was made, so the step was not taken.</summary>
    Stale = 3,
    /// <summary>A deletion held back by a safety rule; see <see cref="StorageSyncAction.WithheldReason"/>.</summary>
    Withheld = 4
}
