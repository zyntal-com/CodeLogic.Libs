using CodeLogic.Core.Logging;

namespace CL.Internal;

/// <summary>Observes asynchronous cleanup started by a synchronous disposal path.</summary>
internal static class AsyncCleanup
{
    internal static void Observe(Task cleanup, ILogger? logger)
    {
        _ = ReportAsync(cleanup, logger);
    }

    private static async Task ReportAsync(Task cleanup, ILogger? logger)
    {
        try { await cleanup.ConfigureAwait(false); }
        catch (Exception ex) { logger?.Error($"Asynchronous cleanup failed: {LogText.Sanitize(ex.Message, 1024)}"); }
    }
}
