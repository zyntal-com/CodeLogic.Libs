using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using CodeLogic.Core.Results;

namespace CL.Common.Assemblies;

/// <summary>
/// Provides reflection utilities for inspecting types, creating instances,
/// and invoking methods dynamically.
/// </summary>
public static class ReflectionHelper
{
    // ── Instance creation ─────────────────────────────────────────────────────

    /// <summary>
    /// Creates an instance of <typeparamref name="T"/> using its parameterless constructor.
    /// </summary>
    public static Result<T> CreateInstance<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>() where T : class
    {
        try
        {
            var instance = Activator.CreateInstance<T>();
            return instance;
        }
        catch (Exception ex) { return Error.Internal(ErrorCode.Internal, $"Cannot create {typeof(T).Name}", ex.Message); }
    }

    /// <summary>Creates an instance of the given type with optional constructor arguments.</summary>
    public static Result<object> CreateInstance([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type type, params object?[] args)
    {
        try
        {
            var instance = Activator.CreateInstance(type, args)
                ?? throw new InvalidOperationException("Activator returned null");
            return instance;
        }
        catch (Exception ex) { return Error.Internal(ErrorCode.Internal, $"Cannot create {type.Name}", ex.Message); }
    }

    // ── Property access ───────────────────────────────────────────────────────

    /// <summary>Gets a property value by name using reflection.</summary>
    [RequiresUnreferencedCode("Looks members up on the runtime type of the object; trimming may remove them.")]
    public static Result<object?> GetPropertyValue(object obj, string propertyName)
    {
        var prop = obj.GetType().GetProperty(propertyName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (prop is null)
            return Error.NotFound(ErrorCode.NotFound, $"Property '{propertyName}' not found on {obj.GetType().Name}");
        try { return prop.GetValue(obj); }
        catch (Exception ex) { return Error.Internal(ErrorCode.Internal, $"Cannot get '{propertyName}'", ex.Message); }
    }

    /// <summary>Sets a property value by name using reflection.</summary>
    [RequiresUnreferencedCode("Looks members up on the runtime type of the object; trimming may remove them.")]
    public static Result SetPropertyValue(object obj, string propertyName, object? value)
    {
        var prop = obj.GetType().GetProperty(propertyName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (prop is null)
            return Error.NotFound(ErrorCode.NotFound, $"Property '{propertyName}' not found on {obj.GetType().Name}");
        if (!prop.CanWrite)
            return Error.Validation(ErrorCode.InvalidState, $"Property '{propertyName}' is read-only");
        try { prop.SetValue(obj, value); return Result.Success(); }
        catch (Exception ex) { return Error.Internal(ErrorCode.Internal, $"Cannot set '{propertyName}'", ex.Message); }
    }

    /// <summary>Returns all public instance properties of <typeparamref name="T"/> as a name → value dictionary.</summary>
    public static Dictionary<string, object?> ToDictionary<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(T obj) where T : class
    {
        var result = new Dictionary<string, object?>();
        foreach (var prop in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            try { result[prop.Name] = prop.GetValue(obj); }
            catch { /* skip inaccessible */ }
        }
        return result;
    }

    // ── Method invocation ─────────────────────────────────────────────────────

    /// <summary>Invokes a public instance method by name with optional arguments.</summary>
    [RequiresUnreferencedCode("Looks members up on the runtime type of the object; trimming may remove them.")]
    public static Result<object?> InvokeMethod(object obj, string methodName, params object?[] args)
    {
        var method = obj.GetType().GetMethod(methodName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (method is null)
            return Error.NotFound(ErrorCode.NotFound, $"Method '{methodName}' not found on {obj.GetType().Name}");
        try { return method.Invoke(obj, args); }
        catch (TargetInvocationException ex)
        { return Error.Internal(ErrorCode.Internal, $"Method '{methodName}' threw an exception", ex.InnerException?.Message ?? ex.Message); }
        catch (Exception ex)
        { return Error.Internal(ErrorCode.Internal, $"Cannot invoke '{methodName}'", ex.Message); }
    }

    // ── Type inspection ───────────────────────────────────────────────────────

    /// <summary>Returns true if <paramref name="type"/> implements <typeparamref name="TInterface"/>.</summary>
    public static bool Implements<TInterface>(Type type) =>
        typeof(TInterface).IsAssignableFrom(type) && type != typeof(TInterface);

    /// <summary>Returns true if <paramref name="type"/> has a parameterless public constructor.</summary>
    public static bool HasDefaultConstructor([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type type) =>
        type.GetConstructor(Type.EmptyTypes) != null;

    /// <summary>Returns the display name of a nullable type (e.g. "int?" instead of "Nullable`1").</summary>
    public static string GetFriendlyName(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying != null) return $"{underlying.Name}?";
        return type.IsGenericType
            ? $"{type.Name[..type.Name.IndexOf('`')]}<{string.Join(", ", type.GetGenericArguments().Select(GetFriendlyName))}>"
            : type.Name;
    }
}
