using CL.Common.Resilience;
using CL.Data.Errors;
using CL.Data.Execution;
using CodeLogic.Core.Results;

namespace CL.Data.Pipeline;

/// <summary>
/// The innermost built-in step: retries transient failures with backoff and jitter, and guards every node with its
/// circuit breaker.
/// <list type="bullet">
/// <item>Never inside a user transaction: the transaction is no longer usable after a failure, so only the caller can retry it.</item>
/// <item>Reads retry every transient failure. Writes retry only when the statement certainly did not take effect: the
/// connection could not be opened, or the server rolled the statement back (deadlock, lock timeout).</item>
/// <item>The breaker counts node failures only (unreachable, connection lost), not deadlocks, timeouts of slow queries or
/// constraint violations. An open circuit fails fast with <c>data.circuit_open</c>.</item>
/// <item>A failed retry returns the last CL.Data error unchanged, so callers keep matching on <c>data.*</c> codes.</item>
/// </list>
/// </summary>
internal sealed class ResilienceMiddleware(RetryPolicy? retry, TimeProvider time) : ICommandMiddleware
{
    public async ValueTask<Result<T>> InvokeAsync<T>(CommandContext context, CommandNext<T> next)
    {
        if (context.InTransaction) return await next.InvokeAsync(context).ConfigureAwait(false);

        var maxAttempts = retry?.Options.MaxAttempts ?? 1;
        for (var attempt = 1; ; attempt++)
        {
            context.Attempt = attempt;
            context.FailedTransient = false;
            context.FailedOpening = false;

            var node = context.Node ?? context.Database.Primary;
            var breaker = node.Breaker;
            CircuitLease lease = default;
            if (breaker is not null && !breaker.TryAcquire(out lease))
                return DataErrors.CircuitOpen(node.Name, $"The circuit for node '{node.Name}' is open after repeated failures; retry later.");

            var result = await next.InvokeAsync(context).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                lease.Success();
                return result;
            }

            var error = result.Error!;
            if (IsNodeFailure(context, error)) lease.Failure();
            else lease.Ignore();

            if (attempt >= maxAttempts || !IsRetryable(context, error) || context.CancellationToken.IsCancellationRequested)
                return result;
            CL.Data.Telemetry.DataTelemetry.Retries.Add(1, new KeyValuePair<string, object?>("db.namespace", context.Database.Name),
                new("error.type", error.Code));
            try
            {
                await Task.Delay(retry!.GetDelay(attempt), time, context.CancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return result;
            }
        }
    }

    private static bool IsNodeFailure(CommandContext context, Error error) =>
        context.FailedOpening || error.Code == DataErrors.UnavailableCode;

    private static bool IsRetryable(CommandContext context, Error error)
    {
        if (!context.FailedTransient && !context.FailedOpening) return false;
        if (!context.IsWrite) return true;
        return context.FailedOpening || error.Code is DataErrors.DeadlockCode or DataErrors.LockTimeoutCode;
    }
}
