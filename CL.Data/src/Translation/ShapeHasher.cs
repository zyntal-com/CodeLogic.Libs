using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;

namespace CL.Data.Translation;

/// <summary>
/// Computes the cache key of an expression: its structure, the literal constants written in code (by value), the
/// types of captured closures (not their contents) and the tables the lambda parameters stand for. Two lambdas
/// with the same key translate to the same SQL shape; they differ only in the values read from their closures,
/// which <see cref="Slots"/> collects in a fixed walk order.
/// </summary>
internal sealed class ShapeHasher
{
    [ThreadStatic] private static ShapeHasher? _cached;

    // Members get small stable ids: cheaper than reading metadata tokens on every walk.
    // Keyed by reference: reflection returns one object per member, and a duplicate could only cost a cache miss.
    private static readonly ConcurrentDictionary<MemberInfo, long> MemberIds = new(ReferenceEqualityComparer.Instance);
    private static long _nextMemberId;

    private byte[] _buffer = new byte[512];
    private int _length;
    private readonly List<ParameterExpression> _parameters = [];
    private readonly List<ConstantExpression> _slots = [];
    private TranslationScope _scope = null!;
    private bool _ok;

    /// <summary>The closure constants found, in walk order (valid until the next <see cref="Compute"/> on this thread).</summary>
    public IReadOnlyList<ConstantExpression> Slots => _slots;

    /// <summary>A hasher for the current thread (reused; not reentrant).</summary>
    public static ShapeHasher ForThread() => _cached ??= new ShapeHasher();

    /// <summary>The key of <paramref name="body"/> in <paramref name="scope"/>, or null when the expression cannot be cached.</summary>
    public UInt128? Compute(Expression body, TranslationScope scope, int mode)
    {
        _length = 0;
        _parameters.Clear();
        _slots.Clear();
        _scope = scope;
        _ok = scope.Group is null;   // grouped queries embed key SQL in the scope; not cached
        if (!_ok) return null;

        Write(mode);
        Write(scope.Dialect.GetType());
        Write((int)scope.Policy);
        Walk(body);
        _scope = null!;
        return _ok ? CL.Common.Hashing.FastHash.XxHash128(_buffer.AsSpan(0, _length)) : null;
    }

    private void Walk(Expression? node)
    {
        if (!_ok) return;
        switch (node)
        {
            case null:
                Write(0);
                break;
            case ParameterExpression parameter:
                Write(1);
                WriteParameter(parameter);
                break;
            case ConstantExpression constant when ByValue(constant.Value):
                Write(2);
                Write(constant.Type);
                WriteValue(constant.Value);
                break;
            case ConstantExpression constant:
                // A closure or other object: its contents are read again on every use, so only its type is part of the shape.
                Write(3);
                Write(constant.Type);
                _slots.Add(constant);
                break;
            case MemberExpression member:
                Write(4);
                Write(member.Member);
                Walk(member.Expression);
                break;
            case MethodCallExpression call:
                Write(5);
                Write(call.Method);
                Walk(call.Object);
                WalkAll(call.Arguments);
                break;
            case UnaryExpression unary:
                Write(6);
                Write((int)unary.NodeType);
                Write(unary.Type);
                Write(unary.Method);
                Walk(unary.Operand);
                break;
            case BinaryExpression binary:
                Write(7);
                Write((int)binary.NodeType);
                Write(binary.Type);
                Write(binary.Method);
                Walk(binary.Left);
                Walk(binary.Right);
                Walk(binary.Conversion);
                break;
            case ConditionalExpression conditional:
                Write(8);
                Write(conditional.Type);
                Walk(conditional.Test);
                Walk(conditional.IfTrue);
                Walk(conditional.IfFalse);
                break;
            case LambdaExpression lambda:
                Write(9);
                Write(lambda.Type);
                Write(lambda.Parameters.Count);
                foreach (var parameter in lambda.Parameters) WriteParameter(parameter);
                Walk(lambda.Body);
                break;
            case NewExpression create:
                Write(10);
                Write(create.Type);
                Write(create.Constructor);
                WalkAll(create.Arguments);
                Write(create.Members?.Count ?? -1);
                if (create.Members is { } members) foreach (var m in members) Write(m);
                break;
            case NewArrayExpression array:
                Write(11);
                Write((int)array.NodeType);
                Write(array.Type);
                WalkAll(array.Expressions);
                break;
            case MemberInitExpression init:
                Write(12);
                Walk(init.NewExpression);
                Write(init.Bindings.Count);
                foreach (var binding in init.Bindings)
                {
                    if (binding is not MemberAssignment assignment)
                    {
                        _ok = false;
                        return;
                    }
                    Write(assignment.Member);
                    Walk(assignment.Expression);
                }
                break;
            case TypeBinaryExpression typeBinary:
                Write(13);
                Write((int)typeBinary.NodeType);
                Write(typeBinary.TypeOperand);
                Walk(typeBinary.Expression);
                break;
            case InvocationExpression invocation:
                Write(14);
                Walk(invocation.Expression);
                WalkAll(invocation.Arguments);
                break;
            case DefaultExpression @default:
                Write(15);
                Write(@default.Type);
                break;
            default:
                _ok = false;   // shapes we do not hash precisely are translated every time
                break;
        }
    }

    private void WalkAll(IReadOnlyList<Expression> nodes)
    {
        Write(nodes.Count);
        for (var i = 0; i < nodes.Count; i++) Walk(nodes[i]);
    }

    /// <summary>A parameter by its first-appearance index; on first appearance also the table it is bound to.</summary>
    private void WriteParameter(ParameterExpression parameter)
    {
        var index = _parameters.IndexOf(parameter);
        if (index >= 0)
        {
            Write(index);
            return;
        }
        _parameters.Add(parameter);
        Write(-_parameters.Count);
        Write(parameter.Type);
        if (_scope.TableOf(parameter) is { } table)
        {
            Write(1);
            Write(table.Map.ClrType);
            WriteString(table.Alias);
        }
        else
        {
            Write(0);   // a nested lambda's own parameter
        }
    }

    /// <summary>Constants hashed by value: anything the translator may inline or branch on.</summary>
    internal static bool ByValue(object? value) =>
        value is null or string or decimal || value.GetType().IsPrimitive || value.GetType().IsEnum;

    // ── Writers (into a reusable buffer, hashed once at the end) ────────────

    private Span<byte> Reserve(int count)
    {
        if (_length + count > _buffer.Length) Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + count));
        var span = _buffer.AsSpan(_length, count);
        _length += count;
        return span;
    }

    private void Write(int value) => System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(Reserve(4), value);

    private void Write(long value) => System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(Reserve(8), value);

    private void Write(Type type) => Write((long)type.TypeHandle.Value);

    private void Write(MemberInfo? member)
    {
        // Runtime handles identify a member within the process; the declaring type separates methods that share
        // code across generic instantiations. Properties use their getter (always present on compiled code).
        switch (member)
        {
            case null:
                Write(0L);
                return;
            case MethodBase method:
                Write(method.DeclaringType ?? typeof(void));
                Write((long)method.MethodHandle.Value);
                return;
            case FieldInfo field:
                Write(field.DeclaringType ?? typeof(void));
                Write((long)field.FieldHandle.Value);
                return;
            case PropertyInfo { GetMethod: { } getter } property:
                Write(property.DeclaringType ?? typeof(void));
                Write((long)getter.MethodHandle.Value);
                return;
            default:
                Write(MemberIds.GetOrAdd(member, static _ => Interlocked.Increment(ref _nextMemberId)));
                return;
        }
    }

    private void WriteString(string? text)
    {
        if (text is null)
        {
            Write(-1);
            return;
        }
        Write(text.Length);
        MemoryMarshal.AsBytes(text.AsSpan()).CopyTo(Reserve(text.Length * 2));
    }

    private void WriteValue(object? value)
    {
        switch (value)
        {
            case null: Write(-1); break;
            case string s: WriteString(s); break;
            case bool b: Write(b ? 1 : 0); break;
            case double d: Write(BitConverter.DoubleToInt64Bits(d)); break;
            case float f: Write(BitConverter.SingleToInt32Bits(f)); break;
            case decimal m:
                Span<int> bits = stackalloc int[4];
                decimal.GetBits(m, bits);
                MemoryMarshal.AsBytes(bits).CopyTo(Reserve(16));
                break;
            case ulong u: Write(unchecked((long)u)); break;
            case char c: Write(c); break;
            case nint n: Write((long)n); break;
            case nuint n: Write(unchecked((long)n)); break;
            case Enum e: WriteString(e.ToString("D")); break;   // any underlying type, including ulong
            default: Write(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)); break;   // other integers and enums
        }
    }
}
