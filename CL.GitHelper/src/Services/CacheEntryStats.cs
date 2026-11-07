using CL.GitHelper.Errors;
using CL.GitHelper.Models;
using CodeLogic.Core.Configuration;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using System.Collections.Concurrent;
using System.Text.Json;

namespace CL.GitHelper.Services;

/// <summary>Statistics for a single cached repository entry.</summary>
public sealed class CacheEntryStats
{
    /// <summary>Repository configuration ID.</summary>
    public string RepositoryId { get; init; } = "";

    /// <summary>How long this entry has been in the cache.</summary>
    public TimeSpan Age { get; init; }

    /// <summary>Time elapsed since the entry was last accessed.</summary>
    public TimeSpan TimeSinceLastAccess { get; init; }

    /// <summary>Whether this entry has exceeded the configured timeout.</summary>
    public bool IsExpired { get; init; }
}
