using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Sync;

/// <summary>Keeps sync baselines between runs; implement it over a database to make two-way sync durable.</summary>
public interface IStorageSyncStateStore
{
    /// <summary>Returns the baseline for a sync, or null before its first run.</summary>
    Task<StorageSyncBaseline?> LoadAsync(string syncId, CancellationToken cancellationToken);

    /// <summary>Saves a baseline if the stored one still has <paramref name="expectedGeneration"/>.</summary>
    /// <returns><see langword="false"/> when another run saved in between.</returns>
    Task<bool> SaveAsync(string syncId, StorageSyncBaseline baseline, long expectedGeneration, CancellationToken cancellationToken);
}
