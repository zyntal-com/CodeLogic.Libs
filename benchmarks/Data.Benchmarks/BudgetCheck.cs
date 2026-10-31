using System.Text.Json;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

namespace Data.Benchmarks;

/// <summary>One benchmark's budget in <c>benchmarks/budgets.json</c>.</summary>
public sealed record Budget
{
    /// <summary>Most bytes allocated per operation (allocations are nearly deterministic, so this is strict).</summary>
    public long? MaxAllocatedBytes { get; init; }

    /// <summary>Most time relative to the category's baseline (Dapper or raw ADO.NET in the same run), which holds across machines.</summary>
    public double? MaxRatio { get; init; }
}

/// <summary>The budgets file.</summary>
public sealed record BudgetFile
{
    /// <summary>Budgets by benchmark method name.</summary>
    public Dictionary<string, Budget> Benchmarks { get; init; } = [];
}

[JsonSerializable(typeof(BudgetFile))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, ReadCommentHandling = JsonCommentHandling.Skip)]
internal sealed partial class BudgetJson : JsonSerializerContext;

/// <summary>
/// <c>--check-budgets [budgets.json]</c>: runs the benchmarks with a short job and fails (exit code 1) when a benchmark
/// allocates more than its budget or runs slower than its ratio to the category baseline.
/// </summary>
public static class BudgetCheck
{
    public static int Run(string path)
    {
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        var budgets = JsonSerializer.Deserialize(File.ReadAllText(path), BudgetJson.Default.BudgetFile)
            ?? throw new InvalidOperationException($"{path} is empty.");

        var config = ManualConfig.CreateMinimumViable()
            .AddDiagnoser(MemoryDiagnoser.Default)
            .AddJob(Job.Default.WithToolchain(InProcessEmitToolchain.Instance).WithWarmupCount(2).WithIterationCount(6).WithLaunchCount(1))
            .AddLogicalGroupRules(BenchmarkLogicalGroupRule.ByCategory);
        var summary = BenchmarkRunner.Run<MySqlBenchmarks>(config);
        return Check(summary, budgets);
    }

    private static int Check(Summary summary, BudgetFile budgets)
    {
        var reports = summary.Reports.Where(r => r.ResultStatistics is not null).ToList();
        var failures = 0;
        Console.WriteLine();
        Console.WriteLine($"{"Benchmark",-28} {"Allocated",12} {"Budget",12} {"Ratio",7} {"Max",6}  Result");
        foreach (var (name, budget) in budgets.Benchmarks.OrderBy(b => b.Key, StringComparer.Ordinal))
        {
            var report = reports.FirstOrDefault(r => r.BenchmarkCase.Descriptor.WorkloadMethod.Name == name);
            if (report is null)
            {
                Console.WriteLine($"{name,-28} {"",12} {"",12} {"",7} {"",6}  MISSING");
                failures++;
                continue;
            }

            var allocated = report.GcStats.GetBytesAllocatedPerOperation(report.BenchmarkCase) ?? 0;
            var category = report.BenchmarkCase.Descriptor.Categories.FirstOrDefault();
            var baseline = reports.FirstOrDefault(r => r.BenchmarkCase.Descriptor.Baseline && r.BenchmarkCase.Descriptor.Categories.Contains(category));
            var ratio = baseline is null || ReferenceEquals(baseline, report) ? (double?)null
                : report.ResultStatistics!.Mean / baseline.ResultStatistics!.Mean;

            List<string> problems = [];
            if (budget.MaxAllocatedBytes is { } maxBytes && allocated > maxBytes) problems.Add($"allocates {allocated} B > {maxBytes} B");
            if (budget.MaxRatio is { } maxRatio && ratio > maxRatio) problems.Add($"ratio {ratio:F3} > {maxRatio:F3}");
            failures += problems.Count > 0 ? 1 : 0;
            Console.WriteLine($"{name,-28} {allocated,12:N0} {budget.MaxAllocatedBytes,12:N0} {ratio,7:F3} {budget.MaxRatio,6:F2}  " +
                (problems.Count == 0 ? "ok" : "OVER BUDGET: " + string.Join("; ", problems)));
        }
        Console.WriteLine(failures == 0 ? "All benchmarks within budget." : $"{failures} benchmark(s) over budget.");
        return failures == 0 ? 0 : 1;
    }
}
