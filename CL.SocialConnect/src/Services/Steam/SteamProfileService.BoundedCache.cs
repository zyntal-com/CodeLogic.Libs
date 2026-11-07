using System.Text.Json;
using System.Text.Json.Serialization;
using CL.SocialConnect.Events;
using CL.SocialConnect.Models;
using CL.SocialConnect.Models.Steam;
using CodeLogic.Core.Events;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;

namespace CL.SocialConnect.Services.Steam;

public sealed partial class SteamProfileService
{

    /// <summary>
    /// A thread-safe cache with a TTL and an upper bound: an expired entry is dropped when read, and when the
    /// cache is full the oldest entries (by insertion time, which is also expiry order) make room for a new one.
    /// </summary>
    private sealed class BoundedCache<T>(TimeSpan ttl, int maxEntries)
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<string, (T data, DateTime cached)> _entries = new(StringComparer.Ordinal);
        // Insertion order; a record is stale when the dictionary holds a newer cached time for its key.
        private Queue<(string key, DateTime cached)> _order = new();

        public bool TryGet(string key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out T data)
        {
            lock (_lock)
            {
                if (_entries.TryGetValue(key, out var entry))
                {
                    if (DateTime.UtcNow - entry.cached <= ttl)
                    {
                        data = entry.data;
                        return true;
                    }
                    _entries.Remove(key);
                }
            }
            data = default;
            return false;
        }

        public void Set(string key, T data)
        {
            var now = DateTime.UtcNow;
            lock (_lock)
            {
                if (!_entries.ContainsKey(key))
                    EvictWhileFull();
                _entries[key] = (data, now);
                _order.Enqueue((key, now));
                // Refreshing the same keys leaves stale records in the queue; compact it before it outgrows the cache.
                if (_order.Count > 2 * maxEntries)
                    _order = new Queue<(string key, DateTime cached)>(_order.Where(IsCurrent));
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _entries.Clear();
                _order.Clear();
            }
        }

        /// <summary>Drops the oldest entries until there is room for one more. Every entry has a queue record, so this ends.</summary>
        private void EvictWhileFull()
        {
            while (_entries.Count >= maxEntries && _order.TryDequeue(out var oldest))
            {
                // A record superseded by a later Set of the same key (or dropped by TryGet) is skipped; the live record is further back.
                if (IsCurrent(oldest))
                    _entries.Remove(oldest.key);
            }
        }

        private bool IsCurrent((string key, DateTime cached) record) =>
            _entries.TryGetValue(record.key, out var current) && current.cached == record.cached;
    }

    // ── Internal response models ──────────────────────────────────────────────

    private class SteamPlayerSummariesResponse
    {
        [JsonPropertyName("response")]
        public SteamPlayerSummariesResult? Response { get; set; }
    }

    private class SteamPlayerSummariesResult
    {
        [JsonPropertyName("players")]
        public List<SteamPlayer>? Players { get; set; }
    }

    private class SteamPlayerBansResponse
    {
        [JsonPropertyName("players")]
        public List<SteamPlayerBans>? Players { get; set; }
    }

    private class SteamOwnedGamesResponse
    {
        [JsonPropertyName("response")]
        public SteamOwnedGamesResult? Response { get; set; }
    }

    private class SteamOwnedGamesResult
    {
        [JsonPropertyName("games")]
        public List<SteamGame>? Games { get; set; }
    }
}
