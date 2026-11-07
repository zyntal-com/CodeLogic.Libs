using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Sync;

/// <summary>How the two sides of a two-way sync changed one path.</summary>
public enum StorageSyncConflictKind
{
    /// <summary>Both sides edited a file they shared.</summary>
    BothModified = 0,
    /// <summary>Both sides created different files at the same path.</summary>
    BothCreated = 1,
    /// <summary>One side deleted a file the other side edited.</summary>
    DeleteVersusModify = 2
}
