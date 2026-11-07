using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CodeLogic.Core.Results;

namespace CL.Storage.Sync;

/// <summary>
/// Synchronizes directory trees across any two storage connections, in two steps: a plan that can be shown,
/// stored, and approved by its digest, then an apply that re-checks every item against the plan. Two-way syncs
/// with a baseline are three-way: edits and deletions made on one side are carried over, and changes on both
/// sides are conflicts resolved by <see cref="StorageSyncOptions.ConflictPolicy"/>.
/// </summary>
public static partial class StorageSync
{

    /// <summary>Details key marking a step skipped because its item changed after planning.</summary>
    private const string StaleKey = "stale";

    /// <summary>
    /// How old the library's staging left in a folder must be before a sync that deletes the folder removes it:
    /// well above any one transfer, so a transfer still writing there is never disturbed.
    /// </summary>
    internal static readonly TimeSpan StaleStagingAge = TimeSpan.FromHours(24);

    /// <summary>How often the version a copy wrote is read back before the step settles for what it knows.</summary>
    private const int ReadBackAttempts = 3;
}
