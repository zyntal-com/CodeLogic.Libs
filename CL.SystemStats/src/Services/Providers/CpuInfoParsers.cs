using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using CL.SystemStats.Models;

namespace CL.SystemStats.Services.Providers;

/// <summary>
/// Platform-neutral parsers behind the providers' CPU information and temperatures, kept free of OS calls so they can
/// be tested on any machine with sample input.
/// </summary>
internal static class CpuInfoParsers
{
    // ── Linux /proc/cpuinfo ──────────────────────────────────────────────────

    /// <summary>
    /// Parses <c>/proc/cpuinfo</c>. Logical processors are the <c>processor</c> entries. Physical cores are the distinct
    /// <c>(physical id, core id)</c> pairs, so hyper-threads of one core and the same core id on two sockets are told
    /// apart; when the file has no topology (many ARM kernels, some VMs) every logical processor counts as a core.
    /// </summary>
    /// <param name="lines">The lines of <c>/proc/cpuinfo</c>.</param>
    /// <param name="architecture">Architecture string for the result.</param>
    /// <param name="maxFrequencyMHz">
    /// The maximum frequency from <c>cpufreq/cpuinfo_max_freq</c> when available; otherwise the highest <c>cpu MHz</c>
    /// (the current clock) is used.
    /// </param>
    public static CpuInfo ParseProcCpuInfo(IEnumerable<string> lines, string architecture, double? maxFrequencyMHz = null)
    {
        string? modelName = null;
        string? vendor = null;
        double currentMHz = 0;
        var logical = 0;
        var cores = new HashSet<(string Package, string Core)>();
        string? package = null, core = null;

        void EndBlock()
        {
            if (core is not null)
                cores.Add((package ?? "0", core));
            package = core = null;
        }

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                EndBlock();
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            switch (key)
            {
                case "processor":
                    EndBlock();          // a block may not be followed by a blank line
                    logical++;
                    break;
                case "physical id":
                    package = value;
                    break;
                case "core id":
                    core = value;
                    break;
                case "model name":
                    modelName ??= value;
                    break;
                case "Processor":        // older ARM kernels: "Processor : ARMv7 Processor rev 4 (v7l)"
                case "Hardware":
                    modelName ??= value;
                    break;
                case "vendor_id":
                    vendor ??= value;
                    break;
                case "CPU implementer":  // ARM: 0x41 = ARM, 0x51 = Qualcomm, ...
                    vendor ??= ArmImplementer(value);
                    break;
                case "cpu MHz":
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz) && mhz > currentMHz)
                        currentMHz = mhz;
                    break;
            }
        }
        EndBlock();

        if (logical == 0) logical = Math.Max(1, Environment.ProcessorCount);
        var physical = cores.Count > 0 ? Math.Min(cores.Count, logical) : logical;

        return new CpuInfo(
            modelName ?? "Unknown",
            physical,
            logical,
            vendor ?? "Unknown",
            maxFrequencyMHz is > 0 ? maxFrequencyMHz.Value : currentMHz,
            architecture);
    }

    /// <summary>Parses <c>cpufreq/cpuinfo_max_freq</c> (kHz) to MHz; null when the text is not a positive number.</summary>
    public static double? ParseMaxFrequencyKHz(string? text) =>
        long.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var khz) && khz > 0 ? khz / 1000.0 : null;

    private static string ArmImplementer(string value) => value.Trim().ToLowerInvariant() switch
    {
        "0x41" => "ARM",
        "0x42" => "Broadcom",
        "0x43" => "Cavium",
        "0x48" => "HiSilicon",
        "0x4e" => "NVIDIA",
        "0x51" => "Qualcomm",
        "0x53" => "Samsung",
        "0x61" => "Apple",
        "0x63" => "Arm China",
        "0xc0" => "Ampere",
        var other => other
    };

    // ── Windows GetLogicalProcessorInformationEx ─────────────────────────────

    private const int RelationProcessorCore = 0;

    /// <summary>
    /// Counts cores in the buffer that <c>GetLogicalProcessorInformationEx(RelationProcessorCore, ...)</c> fills: one
    /// variable-size <c>SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX</c> record per physical core (<c>Relationship</c> at 0,
    /// <c>Size</c> at 4, then <c>PROCESSOR_RELATIONSHIP</c> with <c>GroupCount</c> at 30 and the <c>GROUP_AFFINITY</c>
    /// array at 32). Logical processors are the set bits of the cores' affinity masks.
    /// </summary>
    /// <param name="buffer">The filled buffer (only the returned length).</param>
    /// <param name="pointerSize">Size of <c>KAFFINITY</c>: 8 in a 64-bit process, 4 in a 32-bit one.</param>
    /// <returns>Physical cores and logical processors; (0, 0) for an empty or malformed buffer.</returns>
    public static (int Physical, int Logical) CountWindowsCores(ReadOnlySpan<byte> buffer, int pointerSize)
    {
        var affinitySize = pointerSize + 2 + 6;   // KAFFINITY Mask; WORD Group; WORD Reserved[3]
        var physical = 0;
        var logical = 0;
        var offset = 0;
        while (offset + 8 <= buffer.Length)
        {
            var relationship = BinaryPrimitives.ReadInt32LittleEndian(buffer[offset..]);
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer[(offset + 4)..]);
            if (size < 8 || offset + size > buffer.Length)
                break;

            if (relationship == RelationProcessorCore && size >= 32)
            {
                physical++;
                var record = buffer.Slice(offset, size);
                var groupCount = BinaryPrimitives.ReadUInt16LittleEndian(record[30..]);
                for (var g = 0; g < groupCount; g++)
                {
                    var at = 32 + g * affinitySize;
                    if (at + pointerSize > record.Length) break;
                    var mask = pointerSize == 8
                        ? BinaryPrimitives.ReadUInt64LittleEndian(record[at..])
                        : BinaryPrimitives.ReadUInt32LittleEndian(record[at..]);
                    logical += BitOperations.PopCount(mask);
                }
            }
            offset += size;
        }
        return (physical, logical);
    }

    // ── Linux temperatures ───────────────────────────────────────────────────

    /// <summary>
    /// Reads temperatures from a sysfs tree: <c>{root}/class/thermal/thermal_zone*/{type,temp}</c> and
    /// <c>{root}/class/hwmon/hwmon*/{name,temp*_input,temp*_label}</c>, all in millidegrees Celsius. Unreadable or
    /// implausible values (outside -100..250 °C) are skipped.
    /// </summary>
    /// <param name="sysRoot">The sysfs mount, normally <c>/sys</c>.</param>
    public static IReadOnlyList<TemperatureReading> ReadSysfsTemperatures(string sysRoot)
    {
        var readings = new List<TemperatureReading>();

        var thermal = Path.Combine(sysRoot, "class", "thermal");
        if (Directory.Exists(thermal))
        {
            foreach (var zone in Directory.EnumerateDirectories(thermal, "thermal_zone*").Order(StringComparer.Ordinal))
            {
                if (ReadMilliCelsius(Path.Combine(zone, "temp")) is not { } celsius) continue;
                var type = ReadLine(Path.Combine(zone, "type")) ?? Path.GetFileName(zone);
                readings.Add(new TemperatureReading($"{Path.GetFileName(zone)}:{type}", celsius));
            }
        }

        var hwmon = Path.Combine(sysRoot, "class", "hwmon");
        if (Directory.Exists(hwmon))
        {
            foreach (var chip in Directory.EnumerateDirectories(hwmon, "hwmon*").Order(StringComparer.Ordinal))
            {
                var name = ReadLine(Path.Combine(chip, "name")) ?? Path.GetFileName(chip);
                foreach (var input in Directory.EnumerateFiles(chip, "temp*_input").Order(StringComparer.Ordinal))
                {
                    if (ReadMilliCelsius(input) is not { } celsius) continue;
                    var sensor = Path.GetFileName(input)[..^"_input".Length];
                    var label = ReadLine(Path.Combine(chip, sensor + "_label")) ?? sensor;
                    readings.Add(new TemperatureReading($"{name}:{label}", celsius));
                }
            }
        }

        return readings;
    }

    private static double? ReadMilliCelsius(string path)
    {
        var text = ReadLine(path);
        if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var milli)) return null;
        var celsius = milli / 1000.0;
        return celsius is >= -100 and <= 250 ? Math.Round(celsius, 1) : null;
    }

    private static string? ReadLine(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            // sysfs attributes are a single short line; never read more than a little.
            using var reader = new StreamReader(path);
            var buffer = new char[256];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            var line = new string(buffer, 0, read).Split('\n')[0].Trim();
            return line.Length == 0 ? null : line;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
