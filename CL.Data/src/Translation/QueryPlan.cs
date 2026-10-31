using System.Collections;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using CL.Data.Sql;

namespace CL.Data.Translation;

/// <summary>
/// Caches lambda translations by shape. A repeated query (same structure, same literal constants, new captured
/// values) skips translation: its captured values are read again and bound into the cached SQL tree. Shapes that
/// depend on captured values (a null that becomes <c>IS NULL</c>, the length of an <c>IN</c> list) keep one plan
/// per variant. Grouped-query lambdas and unusual expression nodes are translated every time.
/// </summary>
public static class QueryPlanCache
{
    private static readonly ConcurrentDictionary<UInt128, PlanEntry> Entries = new();
    private static long _hits;
    private static long _misses;

    /// <summary>Turns the cache on or off (on by default). Off, every lambda is translated.</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>Most cached shapes; beyond it new shapes are translated without being cached.</summary>
    public static int Capacity { get; set; } = 4096;

    /// <summary>Most variants kept per shape.</summary>
    public static int MaxVariantsPerShape { get; set; } = 8;

    /// <summary>Translations served from the cache.</summary>
    public static long Hits => Interlocked.Read(ref _hits);

    /// <summary>Translations that ran (cache miss, disabled, or uncacheable shape).</summary>
    public static long Misses => Interlocked.Read(ref _misses);

    /// <summary>Cached shapes.</summary>
    public static int Count => Entries.Count;

    /// <summary>Empties the cache and resets the counters.</summary>
    public static void Clear()
    {
        Entries.Clear();
        Interlocked.Exchange(ref _hits, 0);
        Interlocked.Exchange(ref _misses, 0);
    }

    internal static bool TryGet(UInt128 key, IReadOnlyList<ConstantExpression> slots, out SqlExpr result)
    {
        if (Entries.TryGetValue(key, out var entry))
        {
            var values = new object?[slots.Count];
            for (var i = 0; i < values.Length; i++) values[i] = slots[i].Value;
            foreach (var variant in entry.Variants)
            {
                if (variant.TryBind(values, out result))
                {
                    Interlocked.Increment(ref _hits);
                    return true;
                }
            }
        }
        Interlocked.Increment(ref _misses);
        result = null!;
        return false;
    }

    internal static void Add(UInt128 key, PlanVariant variant)
    {
        if (Entries.TryGetValue(key, out var entry) || (Entries.Count < Capacity && (entry = Entries.GetOrAdd(key, static _ => new PlanEntry())) is not null))
            entry.Add(variant, MaxVariantsPerShape);
    }

    internal static void CountMiss() => Interlocked.Increment(ref _misses);
}

internal sealed class PlanEntry
{
    private readonly Lock _gate = new();

    public PlanVariant[] Variants { get; private set; } = [];

    public void Add(PlanVariant variant, int max)
    {
        lock (_gate)
        {
            if (Variants.Length < max) Variants = [.. Variants, variant];
        }
    }
}

/// <summary>How a recorded root value is shaped before guards and binders see it.</summary>
internal enum RootKind
{
    /// <summary>The value as evaluated.</summary>
    Raw,
    /// <summary>A collection for <c>IN</c>: its non-null items and whether it held a null.</summary>
    InValues,
    /// <summary>A collection as a list (params arrays).</summary>
    List,
}

/// <summary>The non-null items of an <c>IN</c> collection and whether it contained null.</summary>
internal sealed record InValues(List<object?> Values, bool HasNull);

/// <summary>Collects what a translation read from captured values, so a later lambda of the same shape can be bound without translating.</summary>
internal sealed class PlanRecorder
{
    /// <summary>A guard fact meaning "this shape cannot be described compactly; do not cache".</summary>
    public const long Uncacheable = long.MinValue;

    private readonly List<(Expression Expression, RootKind Kind)> _roots = [];
    private readonly List<(int Root, Func<object?, long> Fact, long Expected)> _guards = [];
    private readonly Dictionary<ParameterExpr, (int Root, Func<object?, SqlParameterValue> Binder)> _slots = new(ReferenceEqualityComparer.Instance);
    private bool _uncacheable;

    /// <summary>Evaluates <paramref name="expression"/> as a root; returns the shaped value and its root index.</summary>
    public (object? Value, int Root) Evaluate(Expression expression, RootKind kind)
    {
        var root = _roots.FindIndex(r => ReferenceEquals(r.Expression, expression) && r.Kind == kind);
        if (root < 0)
        {
            _roots.Add((expression, kind));
            root = _roots.Count - 1;
        }
        return (Shape(ValueEvaluator.Evaluate(expression), kind), root);
    }

    /// <summary>Records that the translation depended on <paramref name="fact"/> of a root being <paramref name="expected"/>.</summary>
    public void Guard(int root, Func<object?, long> fact, long expected)
    {
        if (expected == Uncacheable) _uncacheable = true;
        _guards.Add((root, fact, expected));
    }

    /// <summary>Records that <paramref name="parameter"/> is computed from a root by <paramref name="binder"/>.</summary>
    public void Slot(ParameterExpr parameter, int root, Func<object?, SqlParameterValue> binder) => _slots[parameter] = (root, binder);

    /// <summary>The cacheable plan for <paramref name="template"/>, or null when it cannot be replayed.</summary>
    public PlanVariant? Build(SqlExpr template, IReadOnlyList<ConstantExpression> slotConstants)
    {
        if (_uncacheable || PlanBinder.HasSubquery(template)) return null;
        var parameter = Expression.Parameter(typeof(object?[]), "slots");
        var map = new Dictionary<ConstantExpression, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < slotConstants.Count; i++) map[slotConstants[i]] = i;

        var roots = new (Func<object?[], object?> Read, RootKind Kind)[_roots.Count];
        for (var i = 0; i < roots.Length; i++)
        {
            var (expression, kind) = _roots[i];
            var rewritten = new SlotRewriter(map, parameter).Visit(expression);
            var body = rewritten.Type.IsValueType ? Expression.Convert(rewritten, typeof(object)) : rewritten;
            roots[i] = (Expression.Lambda<Func<object?[], object?>>(body, parameter).Compile(), kind);
        }

        var slots = new Dictionary<ParameterExpr, (int, Func<object?, SqlParameterValue>)>(_slots, ReferenceEqualityComparer.Instance);
        var withSlots = new HashSet<SqlExpr>(ReferenceEqualityComparer.Instance);
        PlanBinder.MarkSlotted(template, slots, withSlots);
        return new PlanVariant(template, roots, [.. _guards], slots, withSlots);
    }

    internal static object? Shape(object? value, RootKind kind) => kind switch
    {
        RootKind.Raw => value,
        RootKind.InValues => ToInValues(value),
        RootKind.List => ToList(value),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static List<object?> ToList(object? value)
    {
        if (value is not IEnumerable source) return [];
        var list = new List<object?>(source is ICollection collection ? collection.Count : 4);
        foreach (var item in source) list.Add(item);
        return list;
    }

    private static InValues ToInValues(object? value)
    {
        if (value is not IEnumerable source) return new InValues([], false);
        var list = new List<object?>(source is ICollection collection ? collection.Count : 4);
        var hasNull = false;
        foreach (var item in source)
        {
            if (item is null) hasNull = true;
            else list.Add(item);
        }
        return new InValues(list, hasNull);
    }

    /// <summary>Replaces closure constants by reads from the slot array, so a root evaluates against new closures.</summary>
    private sealed class SlotRewriter(Dictionary<ConstantExpression, int> map, ParameterExpression slots) : ExpressionVisitor
    {
        protected override Expression VisitConstant(ConstantExpression node) =>
            map.TryGetValue(node, out var index)
                ? Expression.Convert(Expression.ArrayIndex(slots, Expression.Constant(index)), node.Type)
                : node;
    }
}

/// <summary>One cached translation: the SQL template, how to read its captured values, and the guards that must hold.</summary>
internal sealed class PlanVariant(
    SqlExpr template,
    (Func<object?[], object?> Read, RootKind Kind)[] roots,
    (int Root, Func<object?, long> Fact, long Expected)[] guards,
    Dictionary<ParameterExpr, (int Root, Func<object?, SqlParameterValue> Binder)> slots,
    HashSet<SqlExpr> withSlots)
{
    /// <summary>Binds <paramref name="slotValues"/> into the template when every guard holds.</summary>
    public bool TryBind(object?[] slotValues, out SqlExpr result)
    {
        result = null!;
        var values = roots.Length == 0 ? [] : new object?[roots.Length];
        try
        {
            for (var i = 0; i < roots.Length; i++) values[i] = PlanRecorder.Shape(roots[i].Read(slotValues), roots[i].Kind);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;   // translating afresh reports the failure with full context
        }

        foreach (var (root, fact, expected) in guards)
            if (fact(values[root]) != expected) return false;

        result = slots.Count == 0 ? template : new PlanBinder(slots, withSlots, values).Bind(template);
        return true;
    }
}

/// <summary>Rebuilds a cached SQL template with fresh parameter values; subtrees without parameters are reused as they are.</summary>
internal readonly struct PlanBinder(
    Dictionary<ParameterExpr, (int Root, Func<object?, SqlParameterValue> Binder)> slots,
    HashSet<SqlExpr> withSlots,
    object?[] values)
{
    public SqlExpr Bind(SqlExpr node)
    {
        if (!withSlots.Contains(node)) return node;
        return node switch
        {
            ParameterExpr p when slots.TryGetValue(p, out var slot) => new ParameterExpr(slot.Binder(values[slot.Root])),
            ParameterExpr or ColumnExpr or LiteralExpr or NullExpr or StarExpr or ExcludedExpr => node,
            BinaryExpr b => new BinaryExpr(b.Operator, Bind(b.Left), Bind(b.Right)),
            UnaryExpr u => new UnaryExpr(u.Operator, Bind(u.Operand)),
            IsNullExpr n => new IsNullExpr(Bind(n.Operand), n.Negated),
            InListExpr l => new InListExpr(Bind(l.Operand), BindAll(l.Values), l.Negated),
            LikeExpr l => new LikeExpr(Bind(l.Operand), Bind(l.Pattern), l.Negated),
            FunctionExpr f => new FunctionExpr(f.Function, BindAll(f.Arguments)),
            AggregateExpr a => a with { Operand = a.Operand is null ? null : Bind(a.Operand) },
            CaseExpr c => new CaseExpr(BindWhens(c.Whens), c.Else is null ? null : Bind(c.Else)),
            InSubqueryExpr or ExistsExpr or SubqueryExpr => throw new InvalidOperationException("Plans with subqueries are never cached."),
        };
    }

    private SqlExpr[] BindAll(IReadOnlyList<SqlExpr> nodes)
    {
        var result = new SqlExpr[nodes.Count];
        for (var i = 0; i < result.Length; i++) result[i] = Bind(nodes[i]);
        return result;
    }

    private CaseWhen[] BindWhens(IReadOnlyList<CaseWhen> whens)
    {
        var result = new CaseWhen[whens.Count];
        for (var i = 0; i < result.Length; i++) result[i] = new CaseWhen(Bind(whens[i].When), Bind(whens[i].Then));
        return result;
    }

    /// <summary>Adds every node that has a bound parameter below it to <paramref name="marked"/>; returns whether <paramref name="node"/> does.</summary>
    public static bool MarkSlotted(SqlExpr node, Dictionary<ParameterExpr, (int, Func<object?, SqlParameterValue>)> slots, HashSet<SqlExpr> marked)
    {
        bool Any(IEnumerable<SqlExpr> nodes)
        {
            var found = false;
            foreach (var n in nodes) found |= MarkSlotted(n, slots, marked);   // visit all, not just up to the first
            return found;
        }

        var has = node switch
        {
            ParameterExpr p => slots.ContainsKey(p),
            ColumnExpr or LiteralExpr or NullExpr or StarExpr or ExcludedExpr or InSubqueryExpr or ExistsExpr or SubqueryExpr => false,
            BinaryExpr b => Any([b.Left, b.Right]),
            UnaryExpr u => MarkSlotted(u.Operand, slots, marked),
            IsNullExpr n => MarkSlotted(n.Operand, slots, marked),
            InListExpr l => Any([l.Operand, .. l.Values]),
            LikeExpr l => Any([l.Operand, l.Pattern]),
            FunctionExpr f => Any(f.Arguments),
            AggregateExpr a => a.Operand is not null && MarkSlotted(a.Operand, slots, marked),
            CaseExpr c => Any([.. c.Whens.SelectMany(w => new[] { w.When, w.Then }), .. (c.Else is null ? [] : new[] { c.Else })]),
        };
        if (has) marked.Add(node);
        return has;
    }

    public static bool HasSubquery(SqlExpr node) => node switch
    {
        InSubqueryExpr or ExistsExpr or SubqueryExpr => true,
        ParameterExpr or ColumnExpr or LiteralExpr or NullExpr or StarExpr or ExcludedExpr => false,
        BinaryExpr b => HasSubquery(b.Left) || HasSubquery(b.Right),
        UnaryExpr u => HasSubquery(u.Operand),
        IsNullExpr n => HasSubquery(n.Operand),
        InListExpr l => HasSubquery(l.Operand) || l.Values.Any(HasSubquery),
        LikeExpr l => HasSubquery(l.Operand) || HasSubquery(l.Pattern),
        FunctionExpr f => f.Arguments.Any(HasSubquery),
        AggregateExpr a => a.Operand is not null && HasSubquery(a.Operand),
        CaseExpr c => c.Whens.Any(w => HasSubquery(w.When) || HasSubquery(w.Then)) || (c.Else is not null && HasSubquery(c.Else)),
    };
}
