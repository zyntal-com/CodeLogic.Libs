using CodeLogic.Core.Results;

namespace CL.Data.Pipeline;

/// <summary>
/// Hooks for application code around every command (auditing logs, SQL comments, policy checks). Implement
/// only the methods you need; the others do nothing. Entity save hooks are added with the write path.
/// </summary>
public interface IDataInterceptor
{
    /// <summary>
    /// Called before a command runs. Change <see cref="CommandContext.Spec"/> to rewrite it, or return an error to
    /// stop it (the error becomes the command's result).
    /// </summary>
    ValueTask<Error?> OnExecutingAsync(CommandContext context) => ValueTask.FromResult<Error?>(null);

    /// <summary>Called after a command ran, with its error when it failed.</summary>
    ValueTask OnExecutedAsync(CommandContext context, Error? error) => ValueTask.CompletedTask;
}

/// <summary>Runs <see cref="IDataInterceptor"/>s; the outermost middleware.</summary>
internal sealed class InterceptorMiddleware(IReadOnlyList<IDataInterceptor> interceptors) : ICommandMiddleware
{
    public async ValueTask<Result<T>> InvokeAsync<T>(CommandContext context, CommandNext<T> next)
    {
        foreach (var interceptor in interceptors)
        {
            if (await interceptor.OnExecutingAsync(context).ConfigureAwait(false) is { } stop)
                return stop;
        }

        var result = await next.InvokeAsync(context).ConfigureAwait(false);

        foreach (var interceptor in interceptors)
            await interceptor.OnExecutedAsync(context, result.Error).ConfigureAwait(false);
        return result;
    }
}
