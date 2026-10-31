using CodeLogic.Core.Results;

namespace CL.Data.Pipeline;

/// <summary>What an entity save does.</summary>
public enum SaveKind
{
    /// <summary>Insert (including insert-ignore).</summary>
    Insert,
    /// <summary>Update by key.</summary>
    Update,
    /// <summary>Insert or update.</summary>
    Upsert,
    /// <summary>Delete (soft or hard).</summary>
    Delete,
}

/// <summary>An entity save as interceptors see it.</summary>
/// <param name="Database">The database (transaction-bound when inside one).</param>
/// <param name="Kind">What the save does.</param>
/// <param name="EntityType">Entity type.</param>
/// <param name="Entities">The entities being saved.</param>
/// <param name="ChangedProperties">For changed-column updates, the properties written; null when every column is written.</param>
public sealed record EntitySaveContext(CL.Data.Execution.Database Database, SaveKind Kind, Type EntityType, IReadOnlyList<object> Entities,
    IReadOnlyList<string>? ChangedProperties);

/// <summary>
/// Hooks for application code around every command (auditing logs, SQL comments, policy checks) and around entity saves
/// (validation, derived values, audit trails). Implement only the methods you need; the others do nothing.
/// </summary>
public interface IDataInterceptor
{
    /// <summary>
    /// Called before entities are written, before their SQL is planned: changes made to the entities are saved. Return an
    /// error to cancel the save (the error becomes its result). Set-based updates and deletes have no entities and do not
    /// call this.
    /// </summary>
    ValueTask<Error?> OnSavingAsync(EntitySaveContext context) => ValueTask.FromResult<Error?>(null);

    /// <summary>Called after entities were written (or failed to be), with the error when the save failed.</summary>
    ValueTask OnSavedAsync(EntitySaveContext context, Error? error) => ValueTask.CompletedTask;

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
