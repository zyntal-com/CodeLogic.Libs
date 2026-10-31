using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CodeLogic.Core.Results;

namespace CL.Common.Data;

/// <summary>
/// Provides JSON serialization, deserialization, file I/O, validation, and merging utilities.
/// Uses <c>System.Text.Json</c> as the primary engine.
/// All operations that can fail return <see cref="Result{T}"/> or <see cref="Result"/>.
/// <para>
/// The overloads that take a <see cref="JsonTypeInfo{T}"/> or a <see cref="JsonSerializerContext"/> (System.Text.Json
/// source generation) are trim- and Native-AOT-safe; the others use reflection and are annotated as such.
/// </para>
/// </summary>
public static class JsonHelper
{
    private const string ReflectionMessage =
        "Reflection-based JSON serialization; use the overload that takes a JsonTypeInfo<T> or JsonSerializerContext for trimming and Native AOT.";

    private static readonly JsonSerializerOptions _compact = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JsonSerializerOptions _indented = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Serializes the given object to a JSON string.</summary>
    /// <typeparam name="T">The type of the object to serialize.</typeparam>
    /// <param name="obj">The object to serialize. May be null.</param>
    /// <param name="indented">When <c>true</c>, output is formatted with indentation. Default: <c>false</c>.</param>
    /// <returns>A <see cref="Result{T}"/> containing the JSON string on success, or a failure result.</returns>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(ReflectionMessage)]
    public static Result<string> Serialize<T>(T obj, bool indented = false)
    {
        try
        {
            return Result<string>.Success(JsonSerializer.Serialize(obj, indented ? _indented : _compact));
        }
        catch (Exception ex)
        {
            return Result<string>.Failure(Error.FromException(ex, ErrorCode.Internal));
        }
    }

    /// <summary>Deserializes a JSON string into an instance of <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The target type.</typeparam>
    /// <param name="json">The JSON string to deserialize.</param>
    /// <returns>A <see cref="Result{T}"/> containing the deserialized value on success, or a failure result.</returns>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(ReflectionMessage)]
    public static Result<T> Deserialize<T>(string json)
    {
        try
        {
            var value = JsonSerializer.Deserialize<T>(json, _compact);
            return value is null
                ? Result<T>.Failure(Error.Internal(ErrorCode.Internal, "Deserialization returned null."))
                : Result<T>.Success(value);
        }
        catch (Exception ex)
        {
            return Result<T>.Failure(Error.FromException(ex, ErrorCode.Internal));
        }
    }

    /// <summary>Serializes an object to JSON and writes it to the specified file path.</summary>
    /// <typeparam name="T">The type of the object to serialize.</typeparam>
    /// <param name="obj">The object to serialize and write.</param>
    /// <param name="filePath">The file path to write to. The parent directory is created if it does not exist.</param>
    /// <param name="indented">When <c>true</c>, the written JSON is formatted with indentation. Default: <c>true</c>.</param>
    /// <returns>A <see cref="Result"/> indicating success or failure.</returns>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(ReflectionMessage)]
    public static async Task<Result> SerializeToFile<T>(T obj, string filePath, bool indented = true)
    {
        try
        {
            var json = JsonSerializer.Serialize(obj, indented ? _indented : _compact);
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(filePath, json);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure(Error.FromException(ex, ErrorCode.FileWriteFailed));
        }
    }

    /// <summary>Reads a JSON file from disk and deserializes its contents into an instance of <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The target type.</typeparam>
    /// <param name="filePath">The path to the JSON file to read.</param>
    /// <returns>A <see cref="Result{T}"/> containing the deserialized value on success, or a failure result.</returns>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(ReflectionMessage)]
    public static async Task<Result<T>> DeserializeFromFile<T>(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return Result<T>.Failure(Error.NotFound(ErrorCode.FileNotFound, $"JSON file not found: {filePath}"));

            var json = await File.ReadAllTextAsync(filePath);
            return Deserialize<T>(json);
        }
        catch (Exception ex)
        {
            return Result<T>.Failure(Error.FromException(ex, ErrorCode.FileReadFailed));
        }
    }

    // -- Source-generated (trim / Native AOT safe) --------------------------------

    /// <summary>Serializes <paramref name="obj"/> with source-generated metadata (trim- and AOT-safe).</summary>
    /// <typeparam name="T">The type of the object to serialize.</typeparam>
    /// <param name="obj">The object to serialize. May be null.</param>
    /// <param name="typeInfo">The source-generated metadata for <typeparamref name="T"/>; its options decide naming and indentation.</param>
    /// <returns>The JSON string, or a failure.</returns>
    public static Result<string> Serialize<T>(T obj, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        try
        {
            return JsonSerializer.Serialize(obj, typeInfo);
        }
        catch (Exception ex)
        {
            return Error.FromException(ex, ErrorCode.Internal);
        }
    }

    /// <summary>Serializes <paramref name="obj"/> using the metadata for <typeparamref name="T"/> in <paramref name="context"/> (trim- and AOT-safe).</summary>
    /// <typeparam name="T">The type of the object to serialize; must be registered on the context.</typeparam>
    /// <param name="obj">The object to serialize. May be null.</param>
    /// <param name="context">A source-generated <see cref="JsonSerializerContext"/>.</param>
    /// <returns>The JSON string, or a failure (<c>not_supported</c> when the context has no metadata for <typeparamref name="T"/>).</returns>
    public static Result<string> Serialize<T>(T obj, JsonSerializerContext context) =>
        TypeInfo<T>(context) is { } info ? Serialize(obj, info) : MissingTypeInfo<T>();

    /// <summary>Deserializes <paramref name="json"/> with source-generated metadata (trim- and AOT-safe).</summary>
    /// <typeparam name="T">The target type.</typeparam>
    /// <param name="json">The JSON string to deserialize.</param>
    /// <param name="typeInfo">The source-generated metadata for <typeparamref name="T"/>.</param>
    /// <returns>The value, or a failure (also when the JSON is <c>null</c>).</returns>
    public static Result<T> Deserialize<T>(string json, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        try
        {
            var value = JsonSerializer.Deserialize(json, typeInfo);
            return value is null
                ? Error.Internal(ErrorCode.Internal, "Deserialization returned null.")
                : value;
        }
        catch (Exception ex)
        {
            return Error.FromException(ex, ErrorCode.Internal);
        }
    }

    /// <summary>Deserializes <paramref name="json"/> using the metadata for <typeparamref name="T"/> in <paramref name="context"/> (trim- and AOT-safe).</summary>
    /// <typeparam name="T">The target type; must be registered on the context.</typeparam>
    /// <param name="json">The JSON string to deserialize.</param>
    /// <param name="context">A source-generated <see cref="JsonSerializerContext"/>.</param>
    /// <returns>The value, or a failure.</returns>
    public static Result<T> Deserialize<T>(string json, JsonSerializerContext context) =>
        TypeInfo<T>(context) is { } info ? Deserialize(json, info) : MissingTypeInfo<T>();

    /// <summary>Serializes <paramref name="obj"/> with source-generated metadata and writes it to <paramref name="filePath"/> (trim- and AOT-safe).</summary>
    /// <typeparam name="T">The type of the object to serialize.</typeparam>
    /// <param name="obj">The object to serialize and write.</param>
    /// <param name="filePath">The file path to write to. The parent directory is created if it does not exist.</param>
    /// <param name="typeInfo">The source-generated metadata for <typeparamref name="T"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Success, or a failure.</returns>
    public static async Task<Result> SerializeToFile<T>(T obj, string filePath, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await using var stream = File.Create(filePath);
            await JsonSerializer.SerializeAsync(stream, obj, typeInfo, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Error.Unavailable(ErrorCode.Cancelled, "The write was cancelled.");
        }
        catch (Exception ex)
        {
            return Error.FromException(ex, ErrorCode.FileWriteFailed);
        }
    }

    /// <summary>Reads <paramref name="filePath"/> and deserializes it with source-generated metadata (trim- and AOT-safe).</summary>
    /// <typeparam name="T">The target type.</typeparam>
    /// <param name="filePath">The path to the JSON file to read.</param>
    /// <param name="typeInfo">The source-generated metadata for <typeparamref name="T"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The value, or a failure (<c>io.file_not_found</c> when the file is missing).</returns>
    public static async Task<Result<T>> DeserializeFromFile<T>(string filePath, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        try
        {
            if (!File.Exists(filePath))
                return Error.NotFound(ErrorCode.FileNotFound, $"JSON file not found: {filePath}");
            await using var stream = File.OpenRead(filePath);
            var value = await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken).ConfigureAwait(false);
            return value is null ? Error.Internal(ErrorCode.Internal, "Deserialization returned null.") : value;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Error.Unavailable(ErrorCode.Cancelled, "The read was cancelled.");
        }
        catch (JsonException ex)
        {
            return Error.FromException(ex, ErrorCode.Internal);
        }
        catch (Exception ex)
        {
            return Error.FromException(ex, ErrorCode.FileReadFailed);
        }
    }

    private static JsonTypeInfo<T>? TypeInfo<T>(JsonSerializerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>;
    }

    private static Error MissingTypeInfo<T>() =>
        Error.Validation(ErrorCode.NotSupported, $"The JsonSerializerContext has no metadata for {typeof(T).Name}; add [JsonSerializable(typeof({typeof(T).Name}))].");

    // -- Reflection-free helpers ----------------------------------------------------

    /// <summary>Validates whether the given string is well-formed JSON.</summary>
    /// <param name="json">The string to validate. Returns <c>false</c> for null or whitespace.</param>
    /// <returns><c>true</c> if the string is valid JSON; otherwise <c>false</c>.</returns>
    public static bool IsValidJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try { JsonDocument.Parse(json).Dispose(); return true; }
        catch { return false; }
    }

    /// <summary>
    /// Merges two JSON objects. Properties from <paramref name="overlayJson"/> overwrite matching
    /// properties in <paramref name="baseJson"/>; non-conflicting properties from both are preserved.
    /// </summary>
    /// <param name="baseJson">The base JSON object string.</param>
    /// <param name="overlayJson">The overlay JSON object string whose values take precedence.</param>
    /// <returns>A <see cref="Result{T}"/> containing the merged JSON string on success, or a failure result.</returns>
    public static Result<string> Merge(string baseJson, string overlayJson)
    {
        try
        {
            var baseObj = JsonNode.Parse(baseJson)?.AsObject()
                ?? throw new InvalidOperationException("Base JSON must be a JSON object.");
            var overlayObj = JsonNode.Parse(overlayJson)?.AsObject();
            if (overlayObj is not null)
            {
                foreach (var property in overlayObj)
                    baseObj[property.Key] = property.Value?.DeepClone();
            }
            return Result<string>.Success(baseObj.ToJsonString(_indented));
        }
        catch (Exception ex)
        {
            return Result<string>.Failure(Error.FromException(ex, ErrorCode.Internal));
        }
    }

    /// <summary>Attempts to extract the raw JSON text of a top-level property from a JSON string.</summary>
    /// <param name="json">The JSON object string to read from.</param>
    /// <param name="propertyName">The property name to look up (case-sensitive).</param>
    /// <returns>A <see cref="Result{T}"/> containing the raw JSON value string on success, or a failure result.</returns>
    public static Result<string> GetProperty(string json, string propertyName)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(propertyName, out var element))
                return Result<string>.Success(element.GetRawText());
            return Result<string>.Failure(Error.NotFound(ErrorCode.NotFound, $"Property '{propertyName}' not found."));
        }
        catch (Exception ex)
        {
            return Result<string>.Failure(Error.FromException(ex, ErrorCode.Internal));
        }
    }
}
