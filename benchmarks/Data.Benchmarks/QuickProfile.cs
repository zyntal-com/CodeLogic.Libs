using System.Diagnostics;
using System.Reflection;
using BenchmarkDotNet.Attributes;

namespace Data.Benchmarks;

/// <summary>
/// <c>--quick [filter]</c>: runs the CL.Data and raw ADO.NET benchmark methods of <see cref="SqliteBenchmarks"/> and
/// <see cref="MySqlBenchmarks"/> (and of <see cref="PostgreSqlBenchmarks"/> when <c>CL_DATA_PG_CS</c> is set) in a plain loop for about a second each and prints
/// bytes allocated and mean time per call. It takes seconds instead of the minutes BenchmarkDotNet needs, so it is the tool
/// while optimizing; the allocation figures match BenchmarkDotNet's to within a few bytes, the times are rougher. Numbers
/// for the record still come from a full run.
/// </summary>
public static class QuickProfile
{
    // CL_BENCH_TYPES=1 also prints which types each benchmark allocates most (sampled by the runtime, about one sample per
    // 100 KB), to see where the bytes go.
    private static readonly bool types = Environment.GetEnvironmentVariable("CL_BENCH_TYPES") == "1";

    private sealed class AllocationSampler : System.Diagnostics.Tracing.EventListener
    {
        public readonly Dictionary<string, long> Bytes = [];
        public volatile bool On;

        protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
                EnableEvents(source, System.Diagnostics.Tracing.EventLevel.Verbose, (System.Diagnostics.Tracing.EventKeywords)0x1);
        }

        protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs e)
        {
            if (!On || e.EventName is null || !e.EventName.StartsWith("GCAllocationTick", StringComparison.Ordinal) || e.PayloadNames is null) return;
            var name = e.PayloadNames.IndexOf("TypeName");
            var amount = e.PayloadNames.IndexOf("AllocationAmount64");
            if (name < 0 || amount < 0) return;
            lock (Bytes)
            {
                var type = (string)e.Payload![name]!;
                Bytes[type] = Bytes.GetValueOrDefault(type) + Convert.ToInt64(e.Payload[amount]);
            }
        }
    }

    private static async Task TypesAsync(Func<Task> call, int calls)
    {
        using var sampler = new AllocationSampler();
        var runs = Math.Max(calls, (int)Math.Min(200_000, calls * 8L));
        sampler.On = true;
        for (var i = 0; i < runs; i++) await call();
        sampler.On = false;
        long total;
        KeyValuePair<string, long>[] top;
        lock (sampler.Bytes)
        {
            total = Math.Max(1, sampler.Bytes.Values.Sum());
            top = [.. sampler.Bytes.OrderByDescending(p => p.Value).Take(14)];
        }
        foreach (var (type, bytes) in top) Console.WriteLine($"      {100.0 * bytes / total,5:N1}%  {type}");
    }

    public static async Task<int> RunAsync(string? filter)
    {
        await RunAsync(new SqliteBenchmarks(), filter);
        await RunAsync(new MySqlBenchmarks(), filter);
        if (Environment.GetEnvironmentVariable("CL_DATA_PG_CS") is { Length: > 0 }) await RunAsync(new PostgreSqlBenchmarks(), filter);
        return 0;
    }

    private static async Task RunAsync(object suite, string? filter)
    {
        var type = suite.GetType();
        await Invoke(type, suite, typeof(GlobalSetupAttribute));
        try
        {
            Console.WriteLine($"{type.Name,-34} {"B/op",10} {"us/op",10} {"calls",8}");
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.GetCustomAttribute<BenchmarkAttribute>() is null || method.Name.StartsWith("Dapper_", StringComparison.Ordinal)) continue;
                if (filter is not null && !method.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                var call = (Func<Task>)Delegate.CreateDelegate(typeof(Func<Task>), suite, method);

                // Warm up (JIT tiers, plan caches, the pool), then measure for about a second.
                var warm = Stopwatch.StartNew();
                var warmCalls = 0;
                while (warmCalls < 20 || (warm.ElapsedMilliseconds < 700 && warmCalls < 20_000))
                {
                    await call();
                    warmCalls++;
                }

                var calls = Math.Clamp((int)(warmCalls * 1500L / Math.Max(1, warm.ElapsedMilliseconds)), 20, 200_000);
                GC.Collect();
                var before = GC.GetTotalAllocatedBytes(precise: true);
                var timer = Stopwatch.StartNew();
                for (var i = 0; i < calls; i++) await call();
                timer.Stop();
                var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
                Console.WriteLine($"{method.Name,-34} {allocated / calls,10:N0} {timer.Elapsed.TotalMicroseconds / calls,10:N1} {calls,8:N0}");
                if (types) await TypesAsync(call, calls);
            }
        }
        finally
        {
            await Invoke(type, suite, typeof(GlobalCleanupAttribute));
        }
    }

    private static async Task Invoke(Type type, object suite, Type attribute)
    {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            if (method.GetCustomAttribute(attribute) is not null && method.Invoke(suite, null) is Task task)
                await task;
    }
}
