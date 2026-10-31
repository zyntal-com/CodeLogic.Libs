using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CodeLogic.Core.Results;

namespace CL.Common.Web;

/// <summary>
/// Provides typed, async HTTP request helpers built on <see cref="HttpClient"/>.
/// Uses <see cref="Result{T}"/> for all operations so callers never need to catch exceptions.
/// Create one <see cref="HttpClient"/> instance per base URL and reuse it.
/// </summary>
public static class HttpClientHelper
{
    private const string ReflectionMessage =
        "Reflection-based JSON serialization; use the overload that takes a JsonTypeInfo<T> for trimming and Native AOT.";

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // ── GET ───────────────────────────────────────────────────────────────────

    /// <summary>Sends a GET request and returns the response body as a string.</summary>
    public static async Task<Result<string>> GetStringAsync(HttpClient client, string url,
        Dictionary<string, string>? headers = null)
    {
        try
        {
            using var request = BuildRequest(HttpMethod.Get, url, null, headers);
            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return Error.Internal(ErrorCode.Internal, $"HTTP {(int)response.StatusCode}: {body}");
            return body;
        }
        catch (Exception ex) { return Error.Internal(ErrorCode.ConnectionFailed, "GET request failed", ex.Message); }
    }

    /// <summary>Sends a GET request and deserializes the JSON response to <typeparamref name="T"/>.</summary>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(ReflectionMessage)]
    public static async Task<Result<T>> GetJsonAsync<T>(HttpClient client, string url,
        Dictionary<string, string>? headers = null)
    {
        var result = await GetStringAsync(client, url, headers);
        if (!result.IsSuccess) return result.Error!;
        try
        {
            var value = JsonSerializer.Deserialize<T>(result.Value!, _json);
            return value is null ? Error.Internal(ErrorCode.Internal, "Deserialization returned null") : value;
        }
        catch (Exception ex) { return Error.Internal(ErrorCode.Internal, "JSON deserialization failed", ex.Message); }
    }

    // ── POST ──────────────────────────────────────────────────────────────────

    /// <summary>Sends a POST request with a JSON body and returns the response string.</summary>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(ReflectionMessage)]
    public static async Task<Result<string>> PostJsonAsync<TBody>(HttpClient client, string url,
        TBody body, Dictionary<string, string>? headers = null)
    {
        try
        {
            var json    = JsonSerializer.Serialize(body, _json);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var request = BuildRequest(HttpMethod.Post, url, content, headers);
            using var response = await client.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return Error.Internal(ErrorCode.Internal, $"HTTP {(int)response.StatusCode}: {responseBody}");
            return responseBody;
        }
        catch (Exception ex) { return Error.Internal(ErrorCode.ConnectionFailed, "POST request failed", ex.Message); }
    }

    /// <summary>Sends a POST request and deserializes the JSON response.</summary>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(ReflectionMessage)]
    public static async Task<Result<TResponse>> PostJsonAsync<TBody, TResponse>(HttpClient client,
        string url, TBody body, Dictionary<string, string>? headers = null)
    {
        var result = await PostJsonAsync(client, url, body, headers);
        if (!result.IsSuccess) return result.Error!;
        try
        {
            var value = JsonSerializer.Deserialize<TResponse>(result.Value!, _json);
            return value is null ? Error.Internal(ErrorCode.Internal, "Deserialization returned null") : value;
        }
        catch (Exception ex) { return Error.Internal(ErrorCode.Internal, "JSON deserialization failed", ex.Message); }
    }

    // ── PUT ───────────────────────────────────────────────────────────────────

    /// <summary>Sends a PUT request with a JSON body.</summary>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(ReflectionMessage)]
    public static async Task<Result<string>> PutJsonAsync<TBody>(HttpClient client, string url,
        TBody body, Dictionary<string, string>? headers = null)
    {
        try
        {
            var json    = JsonSerializer.Serialize(body, _json);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var request = BuildRequest(HttpMethod.Put, url, content, headers);
            using var response = await client.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return Error.Internal(ErrorCode.Internal, $"HTTP {(int)response.StatusCode}: {responseBody}");
            return responseBody;
        }
        catch (Exception ex) { return Error.Internal(ErrorCode.ConnectionFailed, "PUT request failed", ex.Message); }
    }

    // ── DELETE ────────────────────────────────────────────────────────────────

    /// <summary>Sends a DELETE request.</summary>
    public static async Task<Result> DeleteAsync(HttpClient client, string url,
        Dictionary<string, string>? headers = null)
    {
        try
        {
            using var request = BuildRequest(HttpMethod.Delete, url, null, headers);
            using var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                return Error.Internal(ErrorCode.Internal, $"HTTP {(int)response.StatusCode}: {body}");
            }
            return Result.Success();
        }
        catch (Exception ex) { return Error.Internal(ErrorCode.ConnectionFailed, "DELETE request failed", ex.Message); }
    }

    // -- Source-generated JSON (trim / Native AOT safe) ----------------------------

    /// <summary>Sends a GET request and deserializes the body with source-generated metadata (trim- and AOT-safe).</summary>
    /// <typeparam name="T">The response type.</typeparam>
    /// <param name="client">The HTTP client.</param>
    /// <param name="url">The request URL.</param>
    /// <param name="typeInfo">Source-generated metadata for <typeparamref name="T"/>.</param>
    /// <param name="headers">Optional request headers.</param>
    /// <returns>The deserialized response, or a failure.</returns>
    public static async Task<Result<T>> GetJsonAsync<T>(HttpClient client, string url, JsonTypeInfo<T> typeInfo,
        Dictionary<string, string>? headers = null)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        var result = await GetStringAsync(client, url, headers).ConfigureAwait(false);
        return result.IsSuccess ? DeserializeBody(result.Value!, typeInfo) : result.Error!;
    }

    /// <summary>Sends a JSON POST request serialized with source-generated metadata (trim- and AOT-safe).</summary>
    /// <typeparam name="TBody">The request body type.</typeparam>
    /// <param name="client">The HTTP client.</param>
    /// <param name="url">The request URL.</param>
    /// <param name="body">The request body.</param>
    /// <param name="bodyTypeInfo">Source-generated metadata for <typeparamref name="TBody"/>.</param>
    /// <param name="headers">Optional request headers.</param>
    /// <returns>The raw response body, or a failure.</returns>
    public static Task<Result<string>> PostJsonAsync<TBody>(HttpClient client, string url, TBody body,
        JsonTypeInfo<TBody> bodyTypeInfo, Dictionary<string, string>? headers = null)
    {
        ArgumentNullException.ThrowIfNull(bodyTypeInfo);
        return SendJsonAsync(client, HttpMethod.Post, url, JsonSerializer.Serialize(body, bodyTypeInfo), headers);
    }

    /// <summary>Sends a JSON POST request and deserializes the response, both with source-generated metadata (trim- and AOT-safe).</summary>
    /// <typeparam name="TBody">The request body type.</typeparam>
    /// <typeparam name="TResponse">The response type.</typeparam>
    /// <param name="client">The HTTP client.</param>
    /// <param name="url">The request URL.</param>
    /// <param name="body">The request body.</param>
    /// <param name="bodyTypeInfo">Source-generated metadata for <typeparamref name="TBody"/>.</param>
    /// <param name="responseTypeInfo">Source-generated metadata for <typeparamref name="TResponse"/>.</param>
    /// <param name="headers">Optional request headers.</param>
    /// <returns>The deserialized response, or a failure.</returns>
    public static async Task<Result<TResponse>> PostJsonAsync<TBody, TResponse>(HttpClient client, string url, TBody body,
        JsonTypeInfo<TBody> bodyTypeInfo, JsonTypeInfo<TResponse> responseTypeInfo, Dictionary<string, string>? headers = null)
    {
        ArgumentNullException.ThrowIfNull(responseTypeInfo);
        var result = await PostJsonAsync(client, url, body, bodyTypeInfo, headers).ConfigureAwait(false);
        return result.IsSuccess ? DeserializeBody(result.Value!, responseTypeInfo) : result.Error!;
    }

    /// <summary>Sends a JSON PUT request serialized with source-generated metadata (trim- and AOT-safe).</summary>
    /// <typeparam name="TBody">The request body type.</typeparam>
    /// <param name="client">The HTTP client.</param>
    /// <param name="url">The request URL.</param>
    /// <param name="body">The request body.</param>
    /// <param name="bodyTypeInfo">Source-generated metadata for <typeparamref name="TBody"/>.</param>
    /// <param name="headers">Optional request headers.</param>
    /// <returns>The raw response body, or a failure.</returns>
    public static Task<Result<string>> PutJsonAsync<TBody>(HttpClient client, string url, TBody body,
        JsonTypeInfo<TBody> bodyTypeInfo, Dictionary<string, string>? headers = null)
    {
        ArgumentNullException.ThrowIfNull(bodyTypeInfo);
        return SendJsonAsync(client, HttpMethod.Put, url, JsonSerializer.Serialize(body, bodyTypeInfo), headers);
    }

    private static async Task<Result<string>> SendJsonAsync(HttpClient client, HttpMethod method, string url, string json,
        Dictionary<string, string>? headers)
    {
        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var request = BuildRequest(method, url, content, headers);
            using var response = await client.SendAsync(request).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Error.Internal(ErrorCode.Internal, $"HTTP {(int)response.StatusCode}: {responseBody}");
            return responseBody;
        }
        catch (Exception ex) { return Error.Internal(ErrorCode.ConnectionFailed, $"{method} request failed", ex.Message); }
    }

    private static Result<T> DeserializeBody<T>(string body, JsonTypeInfo<T> typeInfo)
    {
        try
        {
            var value = JsonSerializer.Deserialize(body, typeInfo);
            return value is null ? Error.Internal(ErrorCode.Internal, "Deserialization returned null") : value;
        }
        catch (Exception ex) { return Error.Internal(ErrorCode.Internal, "JSON deserialization failed", ex.Message); }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static HttpRequestMessage BuildRequest(HttpMethod method, string url,
        HttpContent? content, Dictionary<string, string>? headers)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        if (headers != null)
            foreach (var (key, value) in headers)
                request.Headers.TryAddWithoutValidation(key, value);
        return request;
    }
}
