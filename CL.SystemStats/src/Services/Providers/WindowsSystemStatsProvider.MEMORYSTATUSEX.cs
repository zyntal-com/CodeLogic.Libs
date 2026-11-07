using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CL.SystemStats.Abstractions;
using CL.SystemStats.Models;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;

namespace CL.SystemStats.Services.Providers;

public sealed partial class WindowsSystemStatsProvider
{
    // ── P/Invoke ─────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(int relationshipType, byte[]? buffer, ref uint returnedLength);

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task InitializeAsync()
    {
        try
        {
            _overallCpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            // First read is always 0 — prime the counter
            _overallCpuCounter.NextValue();
            // Per-core counters live as long as the provider, so each reading covers the whole sampling interval.
            _coreCounters = new PerformanceCounter?[Environment.ProcessorCount];
            for (var i = 0; i < _coreCounters.Length; i++)
            {
                try
                {
                    var counter = new PerformanceCounter("Processor", "% Processor Time", i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    counter.NextValue();
                    _coreCounters[i] = counter;
                }
                catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                {
                    _coreCounters[i] = null;   // that core reads 0
                }
            }
            _countersAvailable = true;
            _logger?.Info("WindowsSystemStatsProvider initialized with PerformanceCounters");
        }
        catch (Exception ex)
        {
            _countersAvailable = false;
            _logger?.Warning($"PerformanceCounters unavailable (containers/VMs may not support them): {ex.Message}. CPU stats will return 0.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _sampling.WaitAsync().ConfigureAwait(false);
        try
        {
            _overallCpuCounter?.Dispose();
            _overallCpuCounter = null;
            foreach (var counter in _coreCounters) counter?.Dispose();
            _coreCounters = [];
        }
        finally { _sampling.Release(); }
    }

    // ── CPU Info ─────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// Model, vendor and nominal clock come from the registry (<c>HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0</c>:
    /// <c>ProcessorNameString</c>, <c>VendorIdentifier</c>, <c>~MHz</c>); physical cores and logical processors from
    /// <c>GetLogicalProcessorInformationEx</c>, falling back to <see cref="Environment.ProcessorCount"/>.
    /// </remarks>
    public Task<Result<CpuInfo>> GetCpuInfoAsync()
    {
        try
        {
            string modelName = "Unknown";
            string vendor = "Unknown";
            double mhz = 0;

            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                if (key is not null)
                {
                    if (key.GetValue("ProcessorNameString") is string name && !string.IsNullOrWhiteSpace(name))
                        modelName = name.Trim();
                    if (key.GetValue("VendorIdentifier") is string id && !string.IsNullOrWhiteSpace(id))
                        vendor = id.Trim();
                    if (key.GetValue("~MHz") is int clock && clock > 0)
                        mhz = clock;
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                _logger?.Debug($"CPU registry key not readable: {ex.Message}");
            }

            var (physical, logical) = ReadCoreCounts();
            if (logical <= 0) logical = Environment.ProcessorCount;
            if (physical <= 0) physical = logical;

            return Task.FromResult(Result<CpuInfo>.Success(new CpuInfo(
                modelName, physical, logical, vendor, mhz, RuntimeInformation.ProcessArchitecture.ToString())));
        }
        catch (Exception ex)
        {
            _logger?.Error($"GetCpuInfoAsync failed: {ex.Message}");
            return Task.FromResult(Result<CpuInfo>.Failure(
                Error.Internal(SystemStatsErrorCodes.CpuInfo, "Failed to read CPU info", ex.Message)));
        }
    }

    /// <summary>Physical cores and logical processors from <c>GetLogicalProcessorInformationEx</c>; (0, 0) when it fails.</summary>
    private (int Physical, int Logical) ReadCoreCounts()
    {
        try
        {
            uint length = 0;
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, null, ref length)
                && Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer)
                return (0, 0);
            if (length == 0 || length > 16 * 1024 * 1024) return (0, 0);

            var buffer = new byte[length];
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length))
                return (0, 0);

            return CpuInfoParsers.CountWindowsCores(buffer.AsSpan(0, (int)Math.Min(length, (uint)buffer.Length)), IntPtr.Size);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            _logger?.Debug($"GetLogicalProcessorInformationEx unavailable: {ex.Message}");
            return (0, 0);
        }
    }

    // ── CPU Stats ────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>Samplings are serialised: concurrent calls read the counters one after the other.</remarks>
    public async Task<Result<CpuStats>> GetCpuStatsAsync()
    {
        try
        {
            double overall = 0;
            var perCore = new List<double>();

            if (_countersAvailable && _overallCpuCounter is not null)
            {
                await _sampling.WaitAsync().ConfigureAwait(false);
                try
                {
                    var overallCounter = _overallCpuCounter;
                    if (overallCounter is null)
                        return Result<CpuStats>.Failure(Error.Unavailable(SystemStatsErrorCodes.CpuStats, "The provider has been disposed"));

                    // Allow the counter to stabilise
                    await Task.Delay(_config.CpuSamplingIntervalMs).ConfigureAwait(false);
                    overall = Math.Round(overallCounter.NextValue(), 2);

                    // Per-core counters were primed at initialization, so each reading covers the same interval.
                    foreach (var counter in _coreCounters)
                    {
                        try
                        {
                            perCore.Add(counter is null ? 0.0 : Math.Round(counter.NextValue(), 2));
                        }
                        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                        {
                            perCore.Add(0.0);
                        }
                    }
                }
                finally
                {
                    _sampling.Release();
                }
            }
            else
            {
                // Fallback: no PerformanceCounters available
                overall = 0;
                perCore = Enumerable.Repeat(0.0, Environment.ProcessorCount).ToList();
            }

            return Result<CpuStats>.Success(new CpuStats(overall, perCore, DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            _logger?.Error($"GetCpuStatsAsync failed: {ex.Message}");
            return Result<CpuStats>.Failure(
                Error.Internal(SystemStatsErrorCodes.CpuStats, "Failed to read CPU stats", ex.Message));
        }
    }

    // ── Memory Info ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<Result<MemoryInfo>> GetMemoryInfoAsync()
    {
        try
        {
            var memStatus = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref memStatus))
                return Task.FromResult(Result<MemoryInfo>.Success(new MemoryInfo((long)memStatus.ullTotalPhys)));

            // Fallback to GC info
            long total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            return Task.FromResult(Result<MemoryInfo>.Success(new MemoryInfo(total)));
        }
        catch (Exception ex)
        {
            _logger?.Error($"GetMemoryInfoAsync failed: {ex.Message}");
            return Task.FromResult(Result<MemoryInfo>.Failure(
                Error.Internal(SystemStatsErrorCodes.MemoryInfo, "Failed to read memory info", ex.Message)));
        }
    }
}
