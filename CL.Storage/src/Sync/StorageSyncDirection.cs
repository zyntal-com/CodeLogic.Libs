using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Sync;

/// <summary>Which way a sync makes changes.</summary>
public enum StorageSyncDirection
{
    /// <summary>Copies files that are new or changed at the source; never deletes.</summary>
    Update = 0,
    /// <summary>Makes the destination match the source; deletes destination-only items when <see cref="StorageSyncOptions.DeleteExtraneous"/> is set.</summary>
    Mirror = 1,
    /// <summary>
    /// Changes flow both ways. With a baseline (<see cref="StorageSyncOptions.StateStore"/>) it is a three-way sync:
    /// edits and deletions made on one side are carried to the other, and changes on both sides are conflicts.
    /// Without one, files missing on a side are copied there and files that differ are conflicts.
    /// </summary>
    TwoWay = 2
}
