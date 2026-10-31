using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace CL.Data.Mapping.Reflection;

/// <summary>
/// Accessor for types without generated mapping. Uses compiled delegates where the runtime can generate
/// code and plain reflection otherwise. Slower than a generated accessor; Native AOT apps should rely on
/// the source generator.
/// </summary>
[RequiresUnreferencedCode("Reads the type's properties and attributes at runtime.")]
internal sealed class ReflectionEntityAccessor<T> : IEntityAccessor<T>
{
    private readonly Func<T, object?>[] _getters;
    private readonly Action<T, object?>?[] _setters;
    private readonly ConstructorInfo? _ctor;
    private readonly int[] _ctorColumns;          // for each ctor parameter, the column index it binds
    private readonly bool _parameterless;

    public ReflectionEntityAccessor()
    {
        var type = typeof(T);
        Map = ReflectionMapBuilder.Build(type);
        var properties = Map.Columns.Select(c => type.GetProperty(c.PropertyName, BindingFlags.Public | BindingFlags.Instance)!).ToArray();
        _getters = new Func<T, object?>[properties.Length];
        _setters = new Action<T, object?>?[properties.Length];
        for (var i = 0; i < properties.Length; i++)
        {
            var p = properties[i];
            var settable = p.SetMethod is { IsPublic: true } && !type.IsValueType;
            if (RuntimeFeature.IsDynamicCodeSupported && RuntimeFeature.IsDynamicCodeCompiled)
            {
                _getters[i] = CompileGetter(p);
                _setters[i] = settable ? CompileSetter(p) : null;
            }
            else
            {
                _getters[i] = e => p.GetValue(e);
                _setters[i] = settable ? (e, v) => p.SetValue(e, v) : null;
            }
        }

        _ctor = ReflectionMapBuilder.BindingConstructor(type);
        _parameterless = _ctor is null ? type.IsValueType : _ctor.GetParameters().Length == 0;
        _ctorColumns = _ctor?.GetParameters()
            .Select(p => Map.Columns.FirstOrDefault(c => string.Equals(c.PropertyName, p.Name, StringComparison.OrdinalIgnoreCase))?.Ordinal ?? -1)
            .ToArray() ?? [];

        if (!_parameterless && _ctorColumns.Contains(-1))
            throw new InvalidOperationException(
                $"'{type.Name}' has no parameterless constructor, and its constructor parameters do not all match mapped properties.");
    }

    public EntityMap Map { get; }

    public T Materialize(DbDataReader reader, ReadOnlySpan<int> ordinals)
    {
        var columns = Map.Columns;
        T entity;
        if (_parameterless)
        {
            entity = Activator.CreateInstance<T>();
        }
        else
        {
            var args = new object?[_ctorColumns.Length];
            for (var i = 0; i < args.Length; i++)
            {
                var c = _ctorColumns[i];
                var column = columns[c];
                args[i] = ordinals[c] >= 0 ? Read(reader, ordinals[c], column) : DefaultOf(column.ClrType);
            }
            entity = (T)_ctor!.Invoke(args);
        }

        for (var i = 0; i < columns.Count; i++)
        {
            if (ordinals[i] < 0 || _setters[i] is not { } set) continue;
            if (!_parameterless && Array.IndexOf(_ctorColumns, i) >= 0) continue;
            set(entity, Read(reader, ordinals[i], columns[i]));
        }

        return entity;
    }

    public void WriteColumn(T entity, int column, IColumnValueWriter writer) =>
        writer.Write(Map.Columns[column], _getters[column](entity));

    public object? GetValue(T entity, int column) => _getters[column](entity);

    public void SetValue<TValue>(T entity, int column, TValue value)
    {
        var set = _setters[column] ?? throw new NotSupportedException(
            $"'{typeof(T).Name}.{Map.Columns[column].PropertyName}' has no public setter.");
        var target = Map.Columns[column].ClrType;
        object? boxed = value;
        if (boxed is not null && !target.IsInstanceOfType(boxed))
            boxed = ValueConversion.FromDb(boxed, Nullable.GetUnderlyingType(target) ?? target);
        set(entity, boxed);
    }

    public EntityKey GetKey(T entity)
    {
        var key = Map.Key.Columns;
        return key.Count switch
        {
            0 => default,
            1 => EntityKey.Of(_getters[key[0].Ordinal](entity)),
            _ => EntityKey.Of(key.Select(k => _getters[k.Ordinal](entity)).ToArray()),
        };
    }

    private static object? Read(DbDataReader reader, int ordinal, ColumnMap column)
    {
        if (reader.IsDBNull(ordinal))
        {
            return column.ClrType.IsValueType && Nullable.GetUnderlyingType(column.ClrType) is null
                ? throw new InvalidCastException($"Column {reader.GetName(ordinal)} is NULL but {typeof(T).Name}.{column.PropertyName} is not nullable.")
                : null;
        }
        return ValueConversion.FromDb(reader.GetValue(ordinal), column.ValueType);
    }

    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Only value types reach here; their default instance needs no generated code.")]
    private static object? DefaultOf(Type type) => type.IsValueType && Nullable.GetUnderlyingType(type) is null
        ? RuntimeHelpers.GetUninitializedObject(type)
        : null;

    [RequiresDynamicCode("Compiles expression trees.")]
    private static Func<T, object?> CompileGetter(PropertyInfo property)
    {
        var entity = Expression.Parameter(typeof(T), "e");
        var body = Expression.Convert(Expression.Property(entity, property), typeof(object));
        return Expression.Lambda<Func<T, object?>>(body, entity).Compile();
    }

    [RequiresDynamicCode("Compiles expression trees.")]
    private static Action<T, object?> CompileSetter(PropertyInfo property)
    {
        var entity = Expression.Parameter(typeof(T), "e");
        var value = Expression.Parameter(typeof(object), "v");
        var body = Expression.Assign(Expression.Property(entity, property), Expression.Convert(value, property.PropertyType));
        return Expression.Lambda<Action<T, object?>>(body, entity, value).Compile();
    }
}
