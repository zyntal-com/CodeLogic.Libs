using System.Text;
using CL.Data.Mapping;
using CL.Data.Mapping.Reflection;

namespace Data.Conformance;

/// <summary>
/// Compares the map a generated accessor builds with the map reflection builds for the same type. Generated and
/// reflection mapping must describe every type identically, so switching between them never changes behavior.
/// </summary>
public static class MappingParity
{
    /// <summary>Types in <paramref name="assembly"/> that have a generated accessor registered.</summary>
    public static IReadOnlyList<Type> GeneratedTypes(System.Reflection.Assembly assembly) =>
        EntityAccessorRegistry.GeneratedTypes().Where(t => t.Assembly == assembly).OrderBy(t => t.FullName, StringComparer.Ordinal).ToArray();

    /// <summary>Lines describing how the generated and reflection maps of <paramref name="type"/> differ (empty when identical).</summary>
    public static IReadOnlyList<string> Differences(Type type)
    {
        var generated = Describe(EntityAccessorRegistry.GeneratedMap(type)!);
        var reflection = Describe(ReflectionMap(type));
        List<string> differences = [];
        for (var i = 0; i < Math.Max(generated.Count, reflection.Count); i++)
        {
            var g = i < generated.Count ? generated[i] : "(missing)";
            var r = i < reflection.Count ? reflection[i] : "(missing)";
            if (g != r) differences.Add($"{type.Name}: generated [{g}] vs reflection [{r}]");
        }
        return differences;
    }

    private static EntityMap ReflectionMap(Type type)
    {
        var accessor = Activator.CreateInstance(typeof(ReflectionEntityAccessor<>).MakeGenericType(type))!;
        return (EntityMap)accessor.GetType().GetProperty(nameof(IEntityAccessor<object>.Map))!.GetValue(accessor)!;
    }

    private static List<string> Describe(EntityMap map)
    {
        List<string> lines =
        [
            $"type {map.ClrType} table={map.ExplicitTableName} schema={map.Schema} isTable={map.IsTable} comment={map.Comment}",
            $"key {string.Join(",", map.Key.Columns.Select(c => c.PropertyName))}",
            $"hints {string.Join(";", map.TableHints.Select(Attr))}",
        ];
        lines.AddRange(map.Columns.Select(Column));
        lines.AddRange(map.Indexes.Select(i => $"index {i.Name} unique={i.Unique} [{string.Join(",", i.Columns.Select(c => c.ToString()))}] include=[{string.Join(",", i.Include.Select(c => c.PropertyName))}]"));
        lines.AddRange(map.ForeignKeys.Select(f => $"fk {f.Column.PropertyName} -> {f.ReferencedEntity}{f.ReferencedTable}.{f.ReferencedColumn} {f.OnDelete}/{f.OnUpdate} {f.ConstraintName}"));
        return lines;
    }

    private static string Column(ColumnMap c) => new StringBuilder()
        .Append($"column #{c.Ordinal} {c.PropertyName}: {c.ClrType} name={c.ExplicitName} previous={c.PreviousName} null={c.IsNullable} ")
        .Append($"{c.DataType} size={c.Size} p={c.Precision} s={c.Scale} keyOrder={c.KeyOrder} unique={c.Unique} indexed={c.Indexed} ")
        .Append($"default={c.DefaultSql} comment={c.Comment} native={c.NativeType} roles={c.Roles} retain={c.RetainDays}/{c.RetentionBatchSize} ")
        .Append($"converter={c.Converter?.GetType()} fk={(c.ForeignKey is null ? "" : Attr(c.ForeignKey))} ")
        .Append($"indexes=[{string.Join(";", c.IndexAttributes.Select(Attr))}] hints=[{string.Join(";", c.Hints.Select(Attr))}]")
        .ToString();

    /// <summary>An attribute's type and public property values.</summary>
    private static string Attr(Attribute attribute) =>
        attribute.GetType().Name + "(" + string.Join(",", attribute.GetType().GetProperties()
            .Where(p => p.Name != nameof(Attribute.TypeId) && p.GetIndexParameters().Length == 0)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => $"{p.Name}={Format(p.GetValue(attribute))}")) + ")";

    private static string Format(object? value) => value switch
    {
        null => "null",
        string s => s,
        System.Collections.IEnumerable items => "[" + string.Join(",", items.Cast<object?>().Select(Format)) + "]",
        _ => value.ToString() ?? "",
    };
}
