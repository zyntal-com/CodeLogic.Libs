using CL.GitHelper.Errors;
using CL.GitHelper.Models;
using CodeLogic.Core.Configuration;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using System.Collections.Concurrent;
using System.Text.Json;

namespace CL.GitHelper.Services;

// ── Public cache stats DTOs ───────────────────────────────────────────────────

/// <summary>Snapshot of the repository cache state.</summary>
public sealed class CacheStats
{
    /// <summary>Whether caching is enabled in the configuration.</summary>
    public bool CacheEnabled { get; init; }

    /// <summary>Configured cache entry lifetime in minutes.</summary>
    public int CacheTimeoutMinutes { get; init; }

    /// <summary>Per-entry details.</summary>
    public List<CacheEntryStats> Entries { get; init; } = [];

    /// <summary>Total number of currently cached entries.</summary>
    public int TotalCached => Entries.Count;
}
