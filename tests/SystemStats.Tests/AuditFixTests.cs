using System.Buffers.Binary;
using CL.SystemStats;
using CL.SystemStats.Abstractions;
using CL.SystemStats.Models;
using CL.SystemStats.Services;
using CL.SystemStats.Services.Providers;
using CodeLogic.Core.Results;
using Xunit;

namespace SystemStats.Tests;

// ── /proc/cpuinfo parsing (sample text, any OS) ───────────────────────────────────

public sealed class ProcCpuInfoParserTests
{
    private static IEnumerable<string> Block(int processor, int physicalId, int coreId, string model = "Intel(R) Xeon(R) CPU E5-2680 v4 @ 2.40GHz") =>
    [
        $"processor\t: {processor}",
        "vendor_id\t: GenuineIntel",
        $"model name\t: {model}",
        "cpu MHz\t\t: 1200.000",
        $"physical id\t: {physicalId}",
        "siblings\t: 4",
        $"core id\t\t: {coreId}",
        "cpu cores\t: 2",
        ""
    ];

    [Fact]
    public void Two_sockets_with_hyperthreading_count_distinct_package_core_pairs()
    {
        // 2 sockets x 2 cores x 2 threads; core ids repeat across sockets.
        var lines = new List<string>();
        var p = 0;
        foreach (var socket in new[] { 0, 1 })
            foreach (var thread in new[] { 0, 1 })
                foreach (var core in new[] { 0, 1 })
                    lines.AddRange(Block(p++, socket, core));

        var info = CpuInfoParsers.ParseProcCpuInfo(lines, "X64");

        Assert.Equal(8, info.LogicalCoreCount);
        Assert.Equal(4, info.PhysicalCoreCount);    // not 2 (the socket count) and not 8
        Assert.Equal("GenuineIntel", info.Vendor);
        Assert.Equal("Intel(R) Xeon(R) CPU E5-2680 v4 @ 2.40GHz", info.ModelName);
        Assert.Equal(1200, info.MaxSpeedMHz);
    }

    [Fact]
    public void One_socket_with_four_cores_and_smt()
    {
        var lines = Enumerable.Range(0, 8).SelectMany(i => Block(i, 0, i % 4));

        var info = CpuInfoParsers.ParseProcCpuInfo(lines, "X64", maxFrequencyMHz: 4200);

        Assert.Equal(4, info.PhysicalCoreCount);
        Assert.Equal(8, info.LogicalCoreCount);
        Assert.Equal(4200, info.MaxSpeedMHz);       // cpufreq max wins over the current clock
    }

    [Fact]
    public void Blocks_without_blank_lines_still_split_at_processor()
    {
        var lines = Enumerable.Range(0, 4).SelectMany(i => Block(i, 0, i).Where(l => l.Length > 0));

        Assert.Equal(4, CpuInfoParsers.ParseProcCpuInfo(lines, "X64").PhysicalCoreCount);
    }

    [Fact]
    public void Arm_without_topology_counts_each_processor_as_a_core()
    {
        string[] lines =
        [
            "processor\t: 0", "BogoMIPS\t: 108.00", "CPU implementer\t: 0x41", "CPU part\t: 0xd08", "",
            "processor\t: 1", "BogoMIPS\t: 108.00", "CPU implementer\t: 0x41", "CPU part\t: 0xd08", "",
            "Hardware\t: BCM2835",
        ];

        var info = CpuInfoParsers.ParseProcCpuInfo(lines, "Arm64");

        Assert.Equal(2, info.LogicalCoreCount);
        Assert.Equal(2, info.PhysicalCoreCount);
        Assert.Equal("ARM", info.Vendor);
        Assert.Equal("BCM2835", info.ModelName);
    }

    [Theory]
    [InlineData("3600000\n", 3600.0)]
    [InlineData("  800000 ", 800.0)]
    [InlineData("", null)]
    [InlineData("-5", null)]
    [InlineData("abc", null)]
    public void Max_frequency_is_parsed_from_khz(string text, double? expected) =>
        Assert.Equal(expected, CpuInfoParsers.ParseMaxFrequencyKHz(text));
}

// ── GetLogicalProcessorInformationEx buffer parsing (synthetic buffer, any OS) ────

public sealed class WindowsCoreCountTests
{
    /// <summary>One RelationProcessorCore record with a single GROUP_AFFINITY.</summary>
    private static byte[] CoreRecord(ulong mask, int pointerSize)
    {
        var affinitySize = pointerSize + 8;
        var size = 32 + affinitySize;
        var record = new byte[size];
        BinaryPrimitives.WriteInt32LittleEndian(record, 0);                // RelationProcessorCore
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), (uint)size);
        record[8] = 1;                                                     // LTP_PC_SMT flag
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(30), 1);    // GroupCount
        if (pointerSize == 8) BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(32), mask);
        else BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(32), (uint)mask);
        return record;
    }

    [Theory]
    [InlineData(8)]
    [InlineData(4)]
    public void Cores_and_their_threads_are_counted(int pointerSize)
    {
        byte[] buffer = [.. CoreRecord(0b0011, pointerSize), .. CoreRecord(0b1100, pointerSize), .. CoreRecord(0b1_0000, pointerSize)];

        Assert.Equal((3, 5), CpuInfoParsers.CountWindowsCores(buffer, pointerSize));
    }

    [Fact]
    public void A_truncated_or_empty_buffer_counts_nothing_beyond_whole_records()
    {
        var record = CoreRecord(0b11, 8);
        Assert.Equal((0, 0), CpuInfoParsers.CountWindowsCores([], 8));
        Assert.Equal((1, 2), CpuInfoParsers.CountWindowsCores([.. record, .. record.AsSpan(0, 10)], 8));
    }
}

// ── Windows provider CPU info on the host (Windows only) ──────────────────────────

public sealed class WindowsCpuInfoTests
{
    [Fact]
    public async Task Windows_cpu_info_is_real_data()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var provider = new WindowsSystemStatsProvider(new SystemStatsConfig());

        var info = (await provider.GetCpuInfoAsync()).ValueOrThrow();

        Assert.NotEqual("Windows", info.Vendor);                     // the old placeholder
        Assert.InRange(info.PhysicalCoreCount, 1, info.LogicalCoreCount);
    }
}

// ── sysfs temperatures (fake tree, any OS) ────────────────────────────────────────

public sealed class SysfsTemperatureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cl_sysfs_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void Thermal_zones_and_hwmon_sensors_are_read_in_celsius()
    {
        Write("class/thermal/thermal_zone0/type", "x86_pkg_temp\n");
        Write("class/thermal/thermal_zone0/temp", "45000\n");
        Write("class/thermal/thermal_zone1/temp", "999999999\n");   // implausible: skipped
        Write("class/hwmon/hwmon0/name", "coretemp\n");
        Write("class/hwmon/hwmon0/temp1_input", "51500\n");
        Write("class/hwmon/hwmon0/temp1_label", "Package id 0\n");
        Write("class/hwmon/hwmon0/temp2_input", "garbage\n");       // unreadable: skipped

        var readings = CpuInfoParsers.ReadSysfsTemperatures(_root);

        Assert.Equal(
            [new TemperatureReading("thermal_zone0:x86_pkg_temp", 45.0), new TemperatureReading("coretemp:Package id 0", 51.5)],
            readings);
    }

    [Fact]
    public void A_missing_tree_yields_no_readings() =>
        Assert.Empty(CpuInfoParsers.ReadSysfsTemperatures(_root));
}

// ── SystemStatsService: feature switches and CPU smoothing (fake provider) ────────

public sealed class SystemStatsServiceSwitchTests
{
    private sealed class FakeProvider : ISystemStatsProvider
    {
        public Queue<double> CpuReadings { get; } = new();
        public int ProcessCalls;
        public int TemperatureCalls;

        public Task<Result<CpuInfo>> GetCpuInfoAsync() =>
            Task.FromResult(Result<CpuInfo>.Success(new CpuInfo("m", 1, 1, "v", 0, "X64")));

        public Task<Result<CpuStats>> GetCpuStatsAsync()
        {
            var v = CpuReadings.Count > 0 ? CpuReadings.Dequeue() : 0;
            return Task.FromResult(Result<CpuStats>.Success(new CpuStats(v, [v, v * 2], DateTime.UtcNow)));
        }

        public Task<Result<MemoryInfo>> GetMemoryInfoAsync() => Task.FromResult(Result<MemoryInfo>.Success(new MemoryInfo(100)));

        public Task<Result<MemoryStats>> GetMemoryStatsAsync() =>
            Task.FromResult(Result<MemoryStats>.Success(new MemoryStats(100, 50, 50, 0, 0, 50, DateTime.UtcNow)));

        public Task<Result<TimeSpan>> GetSystemUptimeAsync() => Task.FromResult(Result<TimeSpan>.Success(TimeSpan.FromHours(1)));

        private Task<Result<IReadOnlyList<ProcessStats>>> Processes()
        {
            Interlocked.Increment(ref ProcessCalls);
            return Task.FromResult(Result<IReadOnlyList<ProcessStats>>.Success(
                [new ProcessStats(1, "p", 10, 1, 1, 1, null, null, "Normal")]));
        }

        public Task<Result<ProcessStats>> GetProcessStatsAsync(int processId)
        {
            Interlocked.Increment(ref ProcessCalls);
            return Task.FromResult(Result<ProcessStats>.Success(new ProcessStats(processId, "p", 10, 1, 1, 1, null, null, "Normal")));
        }

        public Task<Result<IReadOnlyList<ProcessStats>>> GetAllProcessesAsync() => Processes();
        public Task<Result<IReadOnlyList<ProcessStats>>> GetTopProcessesByCpuAsync(int topCount) => Processes();
        public Task<Result<IReadOnlyList<ProcessStats>>> GetTopProcessesByMemoryAsync(int topCount) => Processes();
        public Task<Result<SystemSnapshot>> GetSystemSnapshotAsync() => throw new NotSupportedException("the service composes snapshots");

        public Task<Result<IReadOnlyList<TemperatureReading>>> GetTemperaturesAsync()
        {
            Interlocked.Increment(ref TemperatureCalls);
            return Task.FromResult(Result<IReadOnlyList<TemperatureReading>>.Success([new TemperatureReading("t", 40)]));
        }

        public Task InitializeAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static SystemStatsService Service(FakeProvider provider, Action<SystemStatsConfig>? configure = null)
    {
        var config = new SystemStatsConfig { EnableCaching = false };
        configure?.Invoke(config);
        return new SystemStatsService(provider, config);
    }

    [Fact]
    public async Task Process_calls_fail_without_touching_processes_when_monitoring_is_off()
    {
        var provider = new FakeProvider();
        var service = Service(provider, c => c.EnableProcessMonitoring = false);

        var results = new[]
        {
            (await service.GetAllProcessesAsync()).Error,
            (await service.GetTopProcessesByCpuAsync(5)).Error,
            (await service.GetTopProcessesByMemoryAsync(5)).Error,
            (await service.GetProcessStatsAsync(1)).Error,
        };

        Assert.All(results, e =>
        {
            Assert.IsType<UnavailableError>(e);
            Assert.Equal(SystemStatsErrorCodes.ProcessMonitoringDisabled, e!.Code);
        });
        Assert.Equal(0, provider.ProcessCalls);
    }

    [Fact]
    public async Task Snapshots_skip_process_sampling_when_monitoring_is_off()
    {
        var provider = new FakeProvider();
        var service = Service(provider, c => c.EnableProcessMonitoring = false);

        var snap = (await service.GetSystemSnapshotAsync()).ValueOrThrow();

        Assert.Empty(snap.TopProcessesByCpu);
        Assert.Empty(snap.TopProcessesByMemory);
        Assert.Equal(0, provider.ProcessCalls);
    }

    [Fact]
    public async Task Snapshots_include_processes_when_monitoring_is_on()
    {
        var provider = new FakeProvider();
        var service = Service(provider);

        var snap = (await service.GetSystemSnapshotAsync()).ValueOrThrow();

        Assert.Single(snap.TopProcessesByCpu);
        Assert.Single(snap.TopProcessesByMemory);
    }

    [Fact]
    public async Task Temperatures_honour_the_switch()
    {
        var provider = new FakeProvider();

        var off = await Service(provider, c => c.EnableTemperatureMonitoring = false).GetTemperaturesAsync();
        Assert.Equal(SystemStatsErrorCodes.TemperatureMonitoringDisabled, off.Error!.Code);
        Assert.Equal(0, provider.TemperatureCalls);

        var on = await Service(provider).GetTemperaturesAsync();
        Assert.Equal(40, Assert.Single(on.ValueOrThrow()).Celsius);
    }

    [Fact]
    public async Task Cpu_readings_are_averaged_over_the_configured_window()
    {
        var provider = new FakeProvider();
        foreach (var v in new double[] { 10, 20, 30, 40 }) provider.CpuReadings.Enqueue(v);
        var service = Service(provider, c => c.CpuSamplesForAverage = 3);

        var readings = new List<CpuStats>();
        for (var i = 0; i < 4; i++) readings.Add((await service.GetCpuStatsAsync()).ValueOrThrow());

        Assert.Equal([10, 15, 20, 30], readings.Select(r => r.OverallUsagePercent));
        Assert.Equal([20, 30, 40, 60], readings.Select(r => r.PerCoreUsagePercent[1]));
    }

    [Fact]
    public async Task One_sample_means_no_smoothing()
    {
        var provider = new FakeProvider();
        foreach (var v in new double[] { 10, 90 }) provider.CpuReadings.Enqueue(v);
        var service = Service(provider, c => c.CpuSamplesForAverage = 1);

        await service.GetCpuStatsAsync();
        Assert.Equal(90, (await service.GetCpuStatsAsync()).ValueOrThrow().OverallUsagePercent);
    }

    [Fact]
    public void A_core_count_change_restarts_the_window()
    {
        var service = Service(new FakeProvider(), c => c.CpuSamplesForAverage = 5);

        service.AddCpuSample(new CpuStats(10, [10, 10], DateTime.UtcNow));
        var after = service.AddCpuSample(new CpuStats(50, [50, 50, 50], DateTime.UtcNow));

        Assert.Equal(50, after.OverallUsagePercent);
        Assert.Equal(3, after.PerCoreUsagePercent.Count);
    }
}
