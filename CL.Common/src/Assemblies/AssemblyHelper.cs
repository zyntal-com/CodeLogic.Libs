using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using CodeLogic.Core.Results;

namespace CL.Common.Assemblies;

/// <summary>
/// Provides reflection-based utilities for inspecting assemblies and their metadata.
/// </summary>
public static class AssemblyHelper
{
    // ── Assembly metadata ─────────────────────────────────────────────────────

    /// <summary>Returns the name of the assembly containing <paramref name="obj"/>.</summary>
    public static string GetName(object obj) =>
        obj.GetType().Assembly.GetName().Name ?? string.Empty;

    /// <summary>Returns the version of the assembly containing <paramref name="obj"/>.</summary>
    public static string GetVersion(object obj) =>
        obj.GetType().Assembly.GetName().Version?.ToString() ?? string.Empty;

    /// <summary>Returns the <see cref="AssemblyDescriptionAttribute"/> value, or empty string.</summary>
    public static string GetDescription(object obj) =>
        GetAttribute<AssemblyDescriptionAttribute>(obj.GetType().Assembly)?.Description ?? string.Empty;

    /// <summary>Returns the <see cref="AssemblyTitleAttribute"/> value, or empty string.</summary>
    public static string GetTitle(object obj) =>
        GetAttribute<AssemblyTitleAttribute>(obj.GetType().Assembly)?.Title ?? string.Empty;

    /// <summary>Returns the <see cref="AssemblyCompanyAttribute"/> value, or empty string.</summary>
    public static string GetCompany(object obj) =>
        GetAttribute<AssemblyCompanyAttribute>(obj.GetType().Assembly)?.Company ?? string.Empty;

    /// <summary>Returns the <see cref="AssemblyProductAttribute"/> value, or empty string.</summary>
    public static string GetProduct(object obj) =>
        GetAttribute<AssemblyProductAttribute>(obj.GetType().Assembly)?.Product ?? string.Empty;

    /// <summary>Returns the <see cref="AssemblyInformationalVersionAttribute"/> value, or the version string.</summary>
    public static string GetInformationalVersion(object obj)
    {
        var asm = obj.GetType().Assembly;
        return GetAttribute<AssemblyInformationalVersionAttribute>(asm)?.InformationalVersion
               ?? asm.GetName().Version?.ToString()
               ?? string.Empty;
    }

    /// <summary>
    /// Returns a dictionary containing key assembly metadata for the given object's assembly.
    /// Keys: Name, Version, InformationalVersion, Description, Title, Company, Product.
    /// </summary>
    public static Dictionary<string, string> GetMetadata(object obj)
    {
        var asm = obj.GetType().Assembly;
        return new Dictionary<string, string>
        {
            ["Name"]                 = asm.GetName().Name ?? string.Empty,
            ["FullName"]             = asm.GetName().FullName,
            ["Version"]              = asm.GetName().Version?.ToString() ?? string.Empty,
            ["InformationalVersion"] = GetAttribute<AssemblyInformationalVersionAttribute>(asm)?.InformationalVersion ?? string.Empty,
            ["Description"]          = GetAttribute<AssemblyDescriptionAttribute>(asm)?.Description ?? string.Empty,
            ["Title"]                = GetAttribute<AssemblyTitleAttribute>(asm)?.Title ?? string.Empty,
            ["Company"]              = GetAttribute<AssemblyCompanyAttribute>(asm)?.Company ?? string.Empty,
            ["Product"]              = GetAttribute<AssemblyProductAttribute>(asm)?.Product ?? string.Empty,
        };
    }

    // ── Assembly loading ──────────────────────────────────────────────────────

    /// <summary>
    /// Loads an assembly from the given file path.
    /// </summary>
    [RequiresUnreferencedCode("Loads an assembly from disk at run time; trimming cannot see what it depends on.")]
    public static Result<Assembly> LoadFrom(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return Error.NotFound(ErrorCode.FileNotFound, $"Assembly file not found: {filePath}");
            return Assembly.LoadFrom(filePath);
        }
        catch (Exception ex) { return Error.Internal(ErrorCode.Internal, $"Failed to load assembly: {filePath}", ex.Message); }
    }

    /// <summary>
    /// Loads an assembly from the given file and returns metadata about it.
    /// </summary>
    [RequiresUnreferencedCode("Loads an assembly from disk at run time; trimming cannot see what it depends on.")]
    public static Result<Dictionary<string, string>> GetFileMetadata(string filePath)
    {
        var loadResult = LoadFrom(filePath);
        if (!loadResult.IsSuccess) return loadResult.Error!;

        var asm = loadResult.Value!;
        var name = asm.GetName();
        return new Dictionary<string, string>
        {
            ["Name"]        = name.Name ?? string.Empty,
            ["FullName"]    = name.FullName,
            ["Version"]     = name.Version?.ToString() ?? string.Empty,
            ["Description"] = GetAttribute<AssemblyDescriptionAttribute>(asm)?.Description ?? string.Empty,
            ["Location"]    = Path.GetFullPath(filePath),
        };
    }

    // ── Type discovery ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all types in an assembly that implement or inherit from <typeparamref name="T"/>.
    /// </summary>
    [RequiresUnreferencedCode("Enumerates every type in the assembly; trimming may have removed some.")]
    public static IEnumerable<Type> GetImplementors<T>(Assembly assembly) =>
        assembly.GetTypes().Where(t => typeof(T).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

    /// <summary>Returns all public classes in an assembly decorated with <typeparamref name="TAttr"/>.</summary>
    [RequiresUnreferencedCode("Enumerates every type in the assembly; trimming may have removed some.")]
    public static IEnumerable<Type> GetTypesWithAttribute<TAttr>(Assembly assembly)
        where TAttr : Attribute =>
        assembly.GetTypes().Where(t => t.GetCustomAttribute<TAttr>() != null);

    // ── Embedded resources ────────────────────────────────────────────────────

    /// <summary>
    /// Reads an embedded resource from an assembly as a string.
    /// </summary>
    /// <param name="assembly">The assembly containing the resource.</param>
    /// <param name="resourceName">The fully-qualified resource name (e.g. "MyLib.Resources.template.html").</param>
    public static Result<string> ReadEmbeddedResource(Assembly assembly, string resourceName)
    {
        try
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
                return Error.NotFound(ErrorCode.NotFound, $"Embedded resource not found: {resourceName}");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex) { return Error.Internal(ErrorCode.Internal, "Failed to read embedded resource", ex.Message); }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static T? GetAttribute<T>(Assembly assembly) where T : Attribute =>
        assembly.GetCustomAttribute<T>();
}
