using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using CL.Data.Errors;
using CL.Data.Execution;
using CL.Data.Mapping;
using CL.Data.Sql;
using CL.Data.Translation;
using CL.Data.Writes;
using CodeLogic.Core.Results;

namespace CL.Data.Query;

/// <summary>Which terminal a compiled query runs.</summary>
internal enum CompiledTerminal { List, First, Count, Any }

/// <summary>Factory for <see cref="CompiledQuery{T, TArgs}"/>.</summary>
public static class CompiledQuery
{
    /// <summary>
    /// Compiles a query once for repeated use. The query is an expression of a <see cref="QueryBuilder{T}"/> chain whose lambdas
    /// may read <typeparamref name="TArgs"/> (a value, tuple or record): <c>CompiledQuery.For&lt;Product, long&gt;((q, id) =&gt; q.Where(p =&gt; p.Id == id))</c>.
    /// Keep the result in a static field: each call then skips building expression trees, translating and rendering SQL,
    /// and only binds the arguments.
    /// </summary>
    public static CompiledQuery<T, TArgs> For<T, TArgs>(Expression<Func<QueryBuilder<T>, TArgs, QueryBuilder<T>>> query) => new(query);
}

/// <summary>
/// A query planned once per database (and tenant) and replayed with new arguments. Argument-dependent shapes (a null that
/// becomes <c>IS NULL</c>, the length of an <c>IN</c> list) get their own plan. Non-lambda arguments in the chain
/// (<c>Take(10)</c>) must not depend on the arguments.
/// </summary>
/// <typeparam name="T">Entity type.</typeparam>
/// <typeparam name="TArgs">Argument type.</typeparam>
public sealed class CompiledQuery<T, TArgs>
{
    private const int MaxVariants = 8;
    private readonly Expression<Func<QueryBuilder<T>, TArgs, QueryBuilder<T>>> _query;
    private readonly ConditionalWeakTable<Database, ConcurrentDictionary<(CompiledTerminal, object?), Variant[]>> _plans = new();

    internal CompiledQuery(Expression<Func<QueryBuilder<T>, TArgs, QueryBuilder<T>>> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        _query = query;
    }

    /// <summary>Every matching row.</summary>
    public async ValueTask<Result<List<T>>> ToListAsync(Database db, TArgs args, CancellationToken ct = default)
    {
        var variant = Plan(db, CompiledTerminal.List, args, out var spec);
        if (variant.IsFailure) return variant.Error!;
        return await db.QuerySpecAsync(spec, false, variant.Value!.Accessor, variant.Value.Table, variant.Value.Tags, ct).ConfigureAwait(false);
    }

    /// <summary>The first matching row, or null.</summary>
    public async ValueTask<Result<T?>> FirstOrDefaultAsync(Database db, TArgs args, CancellationToken ct = default)
    {
        var variant = Plan(db, CompiledTerminal.First, args, out var spec);
        if (variant.IsFailure) return variant.Error!;
        var rows = await db.QuerySpecAsync(spec, false, variant.Value!.Accessor, variant.Value.Table, variant.Value.Tags, ct).ConfigureAwait(false);
        return rows.IsSuccess ? Result<T?>.Success(rows.Value!.Count > 0 ? rows.Value[0] : default) : rows.Error!;
    }

    /// <summary>Number of matching rows.</summary>
    public async ValueTask<Result<long>> CountAsync(Database db, TArgs args, CancellationToken ct = default)
    {
        var variant = Plan(db, CompiledTerminal.Count, args, out var spec);
        if (variant.IsFailure) return variant.Error!;
        var count = await db.ScalarSpecAsync<long?>(spec, variant.Value!.Table, variant.Value.Tags, ct).ConfigureAwait(false);
        return count.IsSuccess ? Result<long>.Success(count.Value ?? 0) : count.Error!;
    }

    /// <summary>True when any row matches.</summary>
    public async ValueTask<Result<bool>> AnyAsync(Database db, TArgs args, CancellationToken ct = default)
    {
        var variant = Plan(db, CompiledTerminal.Any, args, out var spec);
        if (variant.IsFailure) return variant.Error!;
        var rows = await db.QuerySpecAsync<long>(spec, false, null, variant.Value!.Table, variant.Value.Tags, ct).ConfigureAwait(false);
        return rows.IsSuccess ? Result<bool>.Success(rows.Value!.Count > 0) : rows.Error!;
    }

    // ── Planning ────────────────────────────────────────────────────────────

    /// <summary>One planned shape: rendered SQL and how each of its parameters is computed from the arguments.</summary>
    private sealed record Variant(CompiledRoots? Roots, string Sql, ParameterSource[] Parameters, IEntityAccessor<T> Accessor, string Table, string[] Tags);

    /// <summary>A rendered parameter: fixed (standing filters, literals) or computed from an argument root.</summary>
    private readonly record struct ParameterSource(SqlParameterValue? Fixed, int Root, Func<object?, SqlParameterValue>? Binder);

    private Result<Variant> Plan(Database db, CompiledTerminal terminal, TArgs args, out CommandSpec spec)
    {
        ArgumentNullException.ThrowIfNull(db);
        spec = default;
        var plans = _plans.GetValue(db, static _ => new ConcurrentDictionary<(CompiledTerminal, object?), Variant[]>());
        var key = (terminal, db.CurrentTenant);   // tenant filters bake the tenant into the SQL parameters
        object?[] slots = [args];

        if (plans.TryGetValue(key, out var variants))
        {
            foreach (var candidate in variants)
            {
                if (candidate.Roots is null || !candidate.Roots.TryEvaluate(slots, out var values)) continue;
                spec = Bind(candidate, values);
                return candidate;
            }
        }

        var built = Build(db, terminal, args);
        if (built.IsFailure) return built.Error!;
        var variant = built.Value!;
        if (variant.Roots is not null)
        {
            plans.AddOrUpdate(key, [variant], (_, existing) => existing.Length >= MaxVariants ? existing : [.. existing, variant]);
            if (!variant.Roots.TryEvaluate(slots, out var values))
                return DataErrors.TranslationFailed("A compiled query's arguments could not be evaluated.", typeof(T).Name);
            spec = Bind(variant, values);
        }
        else
        {
            spec = new CommandSpec(variant.Sql, variant.Parameters.Select(p => p.Fixed!).ToArray());   // planned for this call only
        }
        return variant;
    }

    private static CommandSpec Bind(Variant variant, object?[] values)
    {
        var parameters = new SqlParameterValue[variant.Parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var source = variant.Parameters[i];
            parameters[i] = source.Fixed ?? source.Binder!(values[source.Root]);
        }
        return new CommandSpec(variant.Sql, parameters);
    }

    private Result<Variant> Build(Database db, CompiledTerminal terminal, TArgs args)
    {
        QueryBuilder<T> builder;
        try
        {
            builder = (QueryBuilder<T>)Apply(_query.Body, new QueryBuilder<T>(db))!;
        }
        catch (NotSupportedException ex)
        {
            return DataErrors.Unsupported(ex.Message);
        }

        var recorder = new PlanRecorder { Arguments = (_query.Parameters[1], args) };
        Result<(SelectStatement Select, IEntityAccessor<T> Accessor, string Table)> plan;
        var previous = SqlTranslator.Compiling;
        SqlTranslator.Compiling = recorder;
        try
        {
            plan = builder.PlanTerminal(terminal);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return DataErrors.TranslationFailed($"Planning the compiled query failed: {ex.GetType().Name}.", ex.Message);
        }
        finally
        {
            SqlTranslator.Compiling = previous;
        }
        if (plan.IsFailure) return plan.Error!;

        var (select, accessor, table) = plan.Value!;
        var rendered = db.Render(select);
        if (rendered.IsFailure) return rendered.Error!;

        var roots = recorder.IsUncacheable ? null : recorder.CompileRoots([]);
        var bySlot = new Dictionary<SqlParameterValue, (int Root, Func<object?, SqlParameterValue> Binder)>(ReferenceEqualityComparer.Instance);
        if (roots is not null)
            foreach (var (parameter, slot) in roots.Slots) bySlot[parameter.Value] = slot;

        var sources = rendered.Value!.Parameters
            .Select(p => roots is not null && bySlot.TryGetValue(p, out var slot) ? new ParameterSource(null, slot.Root, slot.Binder) : new ParameterSource(p, -1, null))
            .ToArray();
        return new Variant(roots, rendered.Value.Text, sources, accessor, table, [DataTags.Table(table)]);
    }

    /// <summary>Replays the query chain on <paramref name="start"/>; lambdas are passed on unevaluated, so they keep reading the arguments.</summary>
    private object? Apply(Expression node, object start)
    {
        switch (node)
        {
            case ParameterExpression parameter when parameter == _query.Parameters[0]:
                return start;
            case MethodCallExpression call:
            {
                var instance = call.Object is null ? null : Apply(call.Object, start);
                var arguments = new object?[call.Arguments.Count];
                for (var i = 0; i < arguments.Length; i++)
                    arguments[i] = call.Object is null && i == 0 ? Apply(call.Arguments[0], start) : Argument(call.Arguments[i]);
                try
                {
                    return call.Method.Invoke(instance, arguments);
                }
                catch (TargetInvocationException ex) when (ex.InnerException is not null)
                {
                    throw new NotSupportedException($"'{call.Method.Name}' failed while compiling the query: {ex.InnerException.Message}", ex.InnerException);
                }
            }
            default:
                throw new NotSupportedException($"A compiled query must be a chain of query methods on its first parameter; '{node}' is not.");
        }
    }

    private object? Argument(Expression argument)
    {
        if (argument is UnaryExpression { NodeType: ExpressionType.Quote } quote) return quote.Operand;
        if (ParameterDependency.DependsOn(argument, [_query.Parameters[1]]))
            throw new NotSupportedException($"'{argument}' depends on the compiled query's arguments; only lambdas may read them (use a constant here).");
        return ValueEvaluator.Evaluate(argument);
    }
}
