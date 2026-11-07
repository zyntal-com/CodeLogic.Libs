using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CL.SystemStats.Abstractions;
using CL.SystemStats.Models;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;

namespace CL.SystemStats.Services.Providers;

/// <summary>
/// Windows implementation of <see cref="ISystemStatsProvider"/>.
/// Uses <see cref="System.Diagnostics.Process"/>, PerformanceCounters (with graceful fallback),
/// and <c>GlobalMemoryStatusEx</c> P/Invoke for accurate memory figures.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsSystemStatsProvider : ISystemStatsProvider
{

    private const int RelationProcessorCore = 0;
    private const int ErrorInsufficientBuffer = 122;

    // ── Fields ────────────────────────────────────────────────────────────────

    private readonly SystemStatsConfig _config;
    private readonly ILogger? _logger;
    private PerformanceCounter? _overallCpuCounter;
    private PerformanceCounter?[] _coreCounters = [];
    private bool _countersAvailable;
    // PerformanceCounter.NextValue() measures since the previous call on the same counter: two overlapping
    // readings corrupt each other, so samplings are serialised. Never disposed (waiters would hang).
    private readonly SemaphoreSlim _sampling = new(1, 1);

    /// <summary>
    /// Initializes the Windows stats provider.
    /// </summary>
    public WindowsSystemStatsProvider(SystemStatsConfig config, ILogger? logger = null)
    {
        _config = config;
        _logger = logger;
    }
}
