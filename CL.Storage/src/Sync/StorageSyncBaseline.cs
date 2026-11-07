using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Sync;

/// <summary>The state after the last sync: per path, the version each side had. Its generation grows with every save.</summary>
/// <param name="Generation">Grows by one with every saved run; a plan made against an older generation is refused.</param>
/// <param name="Entries">Per-path identities.</param>
public sealed record StorageSyncBaseline(long Generation, IReadOnlyDictionary<string, StorageSyncBaselineEntry> Entries)
{
    /// <summary>An empty baseline, for a first run.</summary>
    public static StorageSyncBaseline Empty { get; } = new(0, new Dictionary<string, StorageSyncBaselineEntry>());
}
