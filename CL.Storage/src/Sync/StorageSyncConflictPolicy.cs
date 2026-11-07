using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Sync;

/// <summary>What a two-way sync does when both sides changed the same file.</summary>
public enum StorageSyncConflictPolicy
{
    /// <summary>Plans a <see cref="StorageSyncActionKind.Conflict"/>; the plan cannot be applied until it is resolved.</summary>
    Block = 0,
    /// <summary>
    /// Keeps both: the source's version keeps the name on both sides, and the destination's version is kept on
    /// both sides as <c>name (conflict xxxxxxxx).ext</c>, named from its identity so repeated runs agree. For
    /// a delete against a modify, the modified file is kept.
    /// </summary>
    KeepBoth = 1,
    /// <summary>
    /// The later modification wins. A pair with equal or unknown times is left alone and reported as a
    /// <see cref="StorageSyncActionKind.Conflict"/>, without holding back the rest of the plan. A modify beats a delete.
    /// </summary>
    NewerWins = 2
}
