using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Sync;

/// <summary>Keeps baselines in memory.</summary>
public sealed class InMemoryStorageSyncStateStore : IStorageSyncStateStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, StorageSyncBaseline> _baselines = [with(StringComparer.Ordinal)];

    /// <inheritdoc />
    public Task<StorageSyncBaseline?> LoadAsync(string syncId, CancellationToken cancellationToken)
    {
        lock (_gate) return Task.FromResult(_baselines.GetValueOrDefault(syncId));
    }

    /// <inheritdoc />
    public Task<bool> SaveAsync(string syncId, StorageSyncBaseline baseline, long expectedGeneration, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var current = _baselines.GetValueOrDefault(syncId)?.Generation ?? 0;
            if (current != expectedGeneration) return Task.FromResult(false);
            _baselines[syncId] = baseline;
            return Task.FromResult(true);
        }
    }
}
