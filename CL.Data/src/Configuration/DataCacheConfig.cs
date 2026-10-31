using CodeLogic.Core.Configuration;

namespace CL.Data.Configuration;

/// <summary>How cache invalidations reach other application nodes.</summary>
public enum CacheInvalidationBus
{
    /// <summary>Single node: invalidations stay in this process.</summary>
    None,
    /// <summary>Published on the CodeLogic event bus (in-process subscribers, or a bridged bus).</summary>
    Events,
    /// <summary>
    /// Written to a small table (<c>cl_data_cache_bus</c>) that every node polls; works across nodes
    /// with no extra infrastructure.
    /// </summary>
    Database,
}

/// <summary>Configuration section <c>data.cache</c>: the unified query and entity cache.</summary>
[ConfigSection("data.cache")]
public sealed class DataCacheConfig : ConfigModelBase
{
    /// <summary>Master switch; a connection can override it with <c>CacheEnabled</c>.</summary>
    [ConfigField(Label = "Enabled", Order = 0)]
    public bool Enabled { get; set; } = true;

    /// <summary>Maximum cached entries (0 = no entry limit).</summary>
    [ConfigField(Label = "Max Entries", Min = 0, Order = 1)]
    public int MaxEntries { get; set; } = 10_000;

    /// <summary>Approximate memory budget in megabytes (0 = no size limit).</summary>
    [ConfigField(Label = "Max Memory (MB)", Min = 0, Order = 2)]
    public int MaxMemoryMb { get; set; } = 256;

    /// <summary>TTL for <c>WithCache()</c> without an explicit policy.</summary>
    [ConfigField(Label = "Default TTL (s)", Min = 1, Order = 3)]
    public int DefaultTtlSeconds { get; set; } = 60;

    /// <summary>
    /// Captured DateTime parameters close to "now" are rounded down to this many seconds so repeated
    /// "recent rows" queries share a cache entry (0 = off).
    /// </summary>
    [ConfigField(Label = "Time Quantization (s)", Min = 0, Order = 4)]
    public int TimeQuantizeSeconds { get; set; } = 5;

    /// <summary>Cache single-row lookups by key (<c>GetByKey</c>/<c>GetById</c>).</summary>
    [ConfigField(Label = "Entity Cache", Order = 5)]
    public bool EntityCache { get; set; }

    /// <summary>TTL for the entity cache.</summary>
    [ConfigField(Label = "Entity Cache TTL (s)", Min = 1, Order = 6)]
    public int EntityCacheTtlSeconds { get; set; } = 300;

    /// <summary>How invalidations reach other nodes.</summary>
    [ConfigField(Label = "Invalidation Bus", Order = 7)]
    public CacheInvalidationBus InvalidationBus { get; set; } = CacheInvalidationBus.None;

    /// <summary>Connection that hosts the <see cref="CacheInvalidationBus.Database"/> table (null = default connection).</summary>
    [ConfigField(Label = "Bus Connection", Order = 8, Collapsed = true)]
    public string? BusConnection { get; set; }

    /// <summary>Milliseconds between polls of the database invalidation bus.</summary>
    [ConfigField(Label = "Bus Poll Interval (ms)", Min = 50, Order = 9, Collapsed = true)]
    public int BusPollIntervalMs { get; set; } = 500;

    /// <summary>Publish cache hit/miss events (frequent; off by default).</summary>
    [ConfigField(Label = "Publish Events", Order = 10, Collapsed = true)]
    public bool PublishEvents { get; set; }

    /// <inheritdoc />
    public override ConfigValidationResult Validate()
    {
        List<ConfigFieldError> errors = [];
        if (MaxEntries < 0) errors.Add(new("maxEntries", "Max entries cannot be negative."));
        if (MaxMemoryMb < 0) errors.Add(new("maxMemoryMb", "Max memory cannot be negative."));
        if (DefaultTtlSeconds < 1) errors.Add(new("defaultTtlSeconds", "Default TTL must be at least 1 second."));
        if (TimeQuantizeSeconds < 0) errors.Add(new("timeQuantizeSeconds", "Time quantization cannot be negative."));
        if (EntityCacheTtlSeconds < 1) errors.Add(new("entityCacheTtlSeconds", "Entity cache TTL must be at least 1 second."));
        if (BusPollIntervalMs < 50) errors.Add(new("busPollIntervalMs", "Bus poll interval must be at least 50 ms."));
        return errors.Count == 0 ? ConfigValidationResult.Valid() : ConfigValidationResult.Invalid(errors);
    }
}
