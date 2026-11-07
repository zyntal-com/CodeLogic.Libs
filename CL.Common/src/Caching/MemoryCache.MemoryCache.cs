using CL.Common.Threading;

namespace CL.Common.Caching;

/// <summary>
/// A string-keyed <see cref="MemoryCache{TKey}"/> (ordinal keys) that adds <see cref="RemoveByPrefix"/> and
/// allocation-free <see cref="TryGet{T}(ReadOnlySpan{char}, out T)"/> lookups.
/// </summary>
public sealed class MemoryCache : MemoryCache<string>, ICache
{
    private readonly Dictionary<string, Entry>.AlternateLookup<ReadOnlySpan<char>> _spanLookup;

    /// <summary>Creates a cache.</summary>
    /// <param name="options">Limits and sweep interval; null uses the defaults.</param>
    /// <param name="timeProvider">Clock and timer source; null uses <see cref="TimeProvider.System"/>.</param>
    public MemoryCache(MemoryCacheOptions? options = null, TimeProvider? timeProvider = null)
        : base(options, timeProvider, StringComparer.Ordinal) =>
        _spanLookup = Entries.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <inheritdoc/>
    public bool TryGet<T>(ReadOnlySpan<char> key, out T? value)
    {
        lock (Sync)
        {
            if (_spanLookup.TryGetValue(key, out var actualKey, out var entry))
                return Hit(actualKey, entry, countStatistics: true, out value);
            Miss();
            value = default;
            return false;
        }
    }

    /// <inheritdoc/>
    public int RemoveByPrefix(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        lock (Sync)
            return RemoveWhere(key => key.StartsWith(prefix, StringComparison.Ordinal));
    }
}
