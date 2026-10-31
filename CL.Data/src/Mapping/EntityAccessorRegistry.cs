using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using CL.Data.Errors;
using CL.Data.Mapping.Reflection;
using CodeLogic.Core.Results;

namespace CL.Data.Mapping;

/// <summary>
/// Finds the accessor of a mapped type. Generated accessors register themselves from a module initializer;
/// other types fall back to reflection unless that is switched off.
/// </summary>
/// <remarks>
/// Native AOT and trimmed apps should switch reflection mapping off, so every entity must have generated
/// mapping: set the MSBuild property <c>&lt;CLDataReflectionMapping&gt;false&lt;/CLDataReflectionMapping&gt;</c>
/// (runtime switch <c>CL.Data.ReflectionMapping</c>). The trimmer then removes the reflection code.
/// </remarks>
public static class EntityAccessorRegistry
{
    private static readonly ConcurrentDictionary<Type, object> Accessors = new();
    private static readonly ConcurrentDictionary<Type, EntityMap> Maps = new();

    /// <summary>The map of <paramref name="type"/> when its accessor has been resolved before (schema sync, foreign keys).</summary>
    public static EntityMap? FindMap(Type type) => Maps.GetValueOrDefault(type);

    /// <summary>
    /// True when types without a generated accessor are mapped by reflection. Off when the app switch
    /// <c>CL.Data.ReflectionMapping</c> is false or <see cref="AllowReflection"/> was set to false.
    /// </summary>
    [FeatureSwitchDefinition("CL.Data.ReflectionMapping")]
    [FeatureGuard(typeof(RequiresUnreferencedCodeAttribute))]
    [UnconditionalSuppressMessage("Trimming", "IL4000",
        Justification = "Trimmed apps set CL.Data.ReflectionMapping=false (MSBuild CLDataReflectionMapping), which the trimmer substitutes as a constant.")]
    public static bool IsReflectionSupported =>
        !AppContext.TryGetSwitch("CL.Data.ReflectionMapping", out var enabled) || enabled;

    /// <summary>Runtime opt-out of reflection mapping (for tests and strict hosts). Default true.</summary>
    public static bool AllowReflection { get; set; } = true;

    /// <summary>Registers a generated accessor. Called by generated code; a later registration replaces an earlier one.</summary>
    public static void Register<T>(IEntityAccessor<T> accessor)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        Accessors[typeof(T)] = accessor;
        Maps[typeof(T)] = accessor.Map;
        Cache<T>.Instance = accessor;
    }

    /// <summary>True when a generated accessor is registered for <typeparamref name="T"/>.</summary>
    public static bool HasGenerated<T>() => Accessors.TryGetValue(typeof(T), out var a) && a is not ReflectionEntityAccessor<T>;

    /// <summary>The accessor of <typeparamref name="T"/>, or <c>data.entity_not_mapped</c>.</summary>
    public static Result<IEntityAccessor<T>> Get<T>()
    {
        if (Cache<T>.Instance is { } cached) return Result<IEntityAccessor<T>>.Success(cached);
        if (Accessors.TryGetValue(typeof(T), out var registered))
        {
            var typed = (IEntityAccessor<T>)registered;
            Maps.TryAdd(typeof(T), typed.Map);
            return Result<IEntityAccessor<T>>.Success(Cache<T>.Instance = typed);
        }

        if (IsReflectionSupported && AllowReflection)
            return CreateReflection<T>();

        return DataErrors.EntityNotMapped(typeof(T).Name);
    }

    /// <summary>Removes cached reflection accessors (tests use this after toggling <see cref="AllowReflection"/>).</summary>
    internal static void ResetReflectionCache<T>()
    {
        if (Cache<T>.Instance is ReflectionEntityAccessor<T>)
        {
            Cache<T>.Instance = null;
            Accessors.TryRemove(typeof(T), out _);
        }
    }

    [RequiresUnreferencedCode("Reflection mapping reads the type's properties and attributes at runtime.")]
    private static Result<IEntityAccessor<T>> CreateReflection<T>()
    {
        try
        {
            var accessor = new ReflectionEntityAccessor<T>();
            Accessors.TryAdd(typeof(T), accessor);
            Maps.TryAdd(typeof(T), accessor.Map);
            return Result<IEntityAccessor<T>>.Success(Cache<T>.Instance = accessor);
        }
        catch (InvalidOperationException ex)
        {
            // An invalid model is a bug in the entity; report it with the full list of problems.
            return DataErrors.Validation($"Type '{typeof(T).Name}' cannot be mapped.", ex.Message);
        }
    }

    private static class Cache<T>
    {
        public static IEntityAccessor<T>? Instance;
    }
}
