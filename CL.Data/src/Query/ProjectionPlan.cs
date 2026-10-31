using System.Data.Common;
using System.Linq.Expressions;
using System.Reflection;
using CL.Data.Errors;
using CL.Data.Mapping;
using CL.Data.Mapping.Conversion;
using CL.Data.Sql;
using CL.Data.Translation;
using CodeLogic.Core.Results;

namespace CL.Data.Query;

/// <summary>
/// A translated projection: the SQL columns to select and how to build a <typeparamref name="TResult"/> from a row.
/// Supports a scalar (<c>x =&gt; x.Name</c>), a constructor (<c>x =&gt; new { x.Id, Total = x.Price * x.Stock }</c>, records)
/// and a member initializer (<c>x =&gt; new Dto { Id = x.Id }</c>). Rows are built with the constructor and property
/// setters named in the expression, so no extra reflection metadata is needed (Native AOT safe).
/// </summary>
internal sealed class ProjectionPlan<TResult>
{
    private enum Shape { Scalar, Construct }

    private readonly Shape _shape;
    private readonly ConstructorInfo? _ctor;
    private readonly int _ctorArgs;
    private readonly MemberInfo[] _members;
    private readonly Type[] _types;
    private readonly ValueConverter?[] _converters;

    private ProjectionPlan(Shape shape, IReadOnlyList<ProjectionItem> columns, ConstructorInfo? ctor, int ctorArgs, MemberInfo[] members,
        Type[] types, ValueConverter?[] converters)
    {
        _shape = shape;
        Columns = columns;
        _ctor = ctor;
        _ctorArgs = ctorArgs;
        _members = members;
        _types = types;
        _converters = converters;
    }

    /// <summary>Projected columns, aliased <c>c0</c>, <c>c1</c>, ...</summary>
    public IReadOnlyList<ProjectionItem> Columns { get; }

    /// <summary>Translates <paramref name="selector"/> with <paramref name="scope"/>.</summary>
    public static Result<ProjectionPlan<TResult>> Create(LambdaExpression selector, TranslationScope scope)
    {
        var body = selector.Body;
        List<Expression> parts = [];
        List<MemberInfo> members = [];
        ConstructorInfo? ctor = null;
        var ctorArgs = 0;
        Shape shape;

        switch (body)
        {
            case NewExpression @new when @new.Constructor is not null:
                shape = Shape.Construct;
                ctor = @new.Constructor;
                parts.AddRange(@new.Arguments);
                ctorArgs = @new.Arguments.Count;
                break;
            case MemberInitExpression init when init.NewExpression.Constructor is not null:
                shape = Shape.Construct;
                ctor = init.NewExpression.Constructor;
                parts.AddRange(init.NewExpression.Arguments);
                ctorArgs = init.NewExpression.Arguments.Count;
                foreach (var binding in init.Bindings)
                {
                    if (binding is not MemberAssignment assignment)
                        return DataErrors.TranslationFailed($"Projection binding '{binding.Member.Name}' must be a simple assignment.");
                    parts.Add(assignment.Expression);
                    members.Add(assignment.Member);
                }
                break;
            case NewExpression or MemberInitExpression:
                return DataErrors.TranslationFailed("A projection must call a constructor.");
            default:
                shape = Shape.Scalar;
                parts.Add(body);
                break;
        }

        List<ProjectionItem> columns = [];
        var types = new Type[parts.Count];
        var converters = new ValueConverter?[parts.Count];
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            if (part is NewExpression or MemberInitExpression)
                return DataErrors.TranslationFailed("Nested objects in a projection are not supported; project flat values.", part.ToString());
            var translated = SqlTranslator.TranslateValue(part, scope);
            if (translated.IsFailure) return translated.Error!;
            columns.Add(new ProjectionItem(translated.Value!, "c" + i));
            types[i] = part.Type;
            converters[i] = SourceColumn(part, scope)?.Converter;
        }

        return new ProjectionPlan<TResult>(shape, columns, ctor, ctorArgs, members.ToArray(), types, converters);
    }

    /// <summary>Builds the result for the current row (columns in projection order).</summary>
    public TResult Materialize(DbDataReader reader)
    {
        if (_shape == Shape.Scalar) return (TResult)Read(reader, 0)!;

        var args = new object?[_ctorArgs];
        for (var i = 0; i < _ctorArgs; i++) args[i] = Read(reader, i);
        var result = _ctor!.Invoke(args);
        for (var m = 0; m < _members.Length; m++)
        {
            var value = Read(reader, _ctorArgs + m);
            switch (_members[m])
            {
                case PropertyInfo property: property.SetValue(result, value); break;
                case FieldInfo field: field.SetValue(result, value); break;
            }
        }
        return (TResult)result;
    }

    private object? Read(DbDataReader reader, int ordinal)
    {
        var type = _types[ordinal];
        if (reader.IsDBNull(ordinal))
        {
            return type.IsValueType && Nullable.GetUnderlyingType(type) is null
                ? throw new InvalidCastException($"Projected value {ordinal} is NULL but {type.Name} is not nullable.")
                : null;
        }

        var raw = reader.GetValue(ordinal);
        var converter = _converters[ordinal] ?? (ValueConverters.TryGet(type, out var registered) ? registered : null);
        return converter is not null
            ? converter.FromStoreObject(ValueConversion.FromDb(raw, converter.StoreType)!)
            : ValueConversion.FromDb(raw, Nullable.GetUnderlyingType(type) ?? type);
    }

    private static ColumnMap? SourceColumn(Expression part, TranslationScope scope)
    {
        while (part is UnaryExpression { NodeType: ExpressionType.Convert } convert) part = convert.Operand;
        return part is MemberExpression { Expression: ParameterExpression parameter } member
            ? scope.TableOf(parameter)?.Map.FindProperty(member.Member.Name)
            : null;
    }
}
