using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Sync;

/// <summary>What a sync does to one path. The numbers are stable: kinds are only ever added at the end.</summary>
public enum StorageSyncActionKind
{
    /// <summary>Copies from source to destination.</summary>
    CopyToDestination = 0,
    /// <summary>Copies from destination to source.</summary>
    CopyToSource = 1,
    /// <summary>Deletes a file, or an empty directory, from the destination.</summary>
    DeleteFromDestination = 2,
    /// <summary>Creates a directory at the destination.</summary>
    CreateDirectory = 3,
    /// <summary>Deletes a file, or an empty directory, from the source (a two-way sync carrying a destination-side deletion).</summary>
    DeleteFromSource = 4,
    /// <summary>Creates a directory at the source.</summary>
    CreateDirectoryAtSource = 5,
    /// <summary>Renames the destination's version to <see cref="StorageSyncAction.TargetPath"/> (a kept conflict copy).</summary>
    RenameAtDestination = 6,
    /// <summary>A conflict left for a person; nothing is changed.</summary>
    Conflict = 7
}
