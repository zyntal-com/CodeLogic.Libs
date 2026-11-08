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

    /// <summary>
    /// The most characters of a failed response body copied into <see cref="Error.Message"/>. Bodies can be
    /// large and may echo request data or secrets, so they are cut to this length.
    /// </summary>
    public const int MaxErrorBodyLength = 256;

    /// <summary>
    /// The error code of a non-success response: <c>http.</c> plus the status code (for example <c>http.404</c>).
    /// </summary>
    public static string StatusErrorCode(int statusCode) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"http.{statusCode}");

    /// <summary>
    /// The error for a non-success response. The kind follows the status: 400/422 validation, 401/403 unauthorized,
    /// 404/410 not found, 409/412 conflict, 408/504 timeout, 429/502/503 unavailable, anything else internal. The code is
    /// <see cref="StatusErrorCode"/>; the message is <c>HTTP {status}: {body}</c> with the body cut to
    /// <see cref="MaxErrorBodyLength"/> characters.
    /// </summary>
    private static Error HttpFailure(HttpResponseMessage response, string body)
    {
        var excerpt = body.Length <= MaxErrorBodyLength
            ? body
            : string.Concat(body.AsSpan(0, MaxErrorBodyLength), $"... ({body.Length} chars)");
        var status = (int)response.StatusCode;
        var code = StatusErrorCode(status);
        var message = $"HTTP {status}: {excerpt}";
        return status switch
        {
            400 or 422 => Error.Validation(code, message),
            401 or 403 => Error.Unauthorized(code, message),
            404 or 410 => Error.NotFound(code, message),
            409 or 412 => Error.Conflict(code, message),
            408 or 504 => Error.Timeout(code, message),
            429 or 502 or 503 => Error.Unavailable(code, message),
            _ => Error.Internal(code, message),
        };
    }

    /// <summary>
    /// The error for a request that got no response: a timeout (<c>connection.timeout</c>), a transport failure
    /// (unavailable, <c>connection.failed</c>), a request that could not be built (invalid URL or header: validation,
    /// <c>invalid_argument</c>), or anything else (internal, <c>connection.failed</c>).
    /// </summary>
    private static Error RequestFailure(string method, Exception ex) => ex switch
    {
        TaskCanceledException or TimeoutException => Error.Timeout(ErrorCode.ConnectionTimeout, $"{method} request timed out", ex.Message),
        HttpRequestException => Error.Unavailable(ErrorCode.ConnectionFailed, $"{method} request failed", ex.Message),
        FormatException or ArgumentException or InvalidOperationException =>
            Error.Validation(ErrorCode.InvalidArgument, $"{method} request is not valid", ex.Message),
        _ => Error.Internal(ErrorCode.ConnectionFailed, $"{method} request failed", ex.Message),
    };

    // ── GET ───────────────────────────────────────────────────────────────────

    /// <summary>Sends a GET request and returns the response body as a string.</summary>
    public static async Task<Result<string>> GetStringAsync(HttpClient client, string url,
        Dictionary<string, string>? headers = null)
    {
        try
        {
            using var request = BuildRequest(HttpMethod.Get, url, null, headers);
            using var response = await client.SendAsync(request).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return HttpFailure(response, body);
            return body;
        }
        catch (Exception ex) { return RequestFailure("GET", ex); }
    }

    /// <summary>Sends a GET request and deserializes the JSON response to <typeparamref name="T"/>.</summary>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(ReflectionMessage)]
    public static async Task<Result<T>> GetJsonAsync<T>(HttpClient client, string url,
        Dictionary<string, string>? headers = null)
    {
        var result = await GetStringAsync(client, url, headers).ConfigureAwait(false);
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
            using var response = await client.SendAsync(request).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return HttpFailure(response, responseBody);
            return responseBody;
        }
        catch (Exception ex) { return RequestFailure("POST", ex); }
    }

    /// <summary>Sends a POST request and deserializes the JSON response.</summary>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(ReflectionMessage)]
    public static async Task<Result<TResponse>> PostJsonAsync<TBody, TResponse>(HttpClient client,
        string url, TBody body, Dictionary<string, string>? headers = null)
    {
        var result = await PostJsonAsync(client, url, body, headers).ConfigureAwait(false);
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
            using var response = await client.SendAsync(request).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return HttpFailure(response, responseBody);
            return responseBody;
        }
        catch (Exception ex) { return RequestFailure("PUT", ex); }
    }

    // ── DELETE ────────────────────────────────────────────────────────────────

    /// <summary>Sends a DELETE request.</summary>
    public static async Task<Result> DeleteAsync(HttpClient client, string url,
        Dictionary<string, string>? headers = null)
    {
        try
        {
            using var request = BuildRequest(HttpMethod.Delete, url, null, headers);
            using var response = await client.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return HttpFailure(response, body);
            }
            return Result.Success();
        }
        catch (Exception ex) { return RequestFailure("DELETE", ex); }
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
                return HttpFailure(response, responseBody);
            return responseBody;
        }
        catch (Exception ex) { return RequestFailure(method.Method, ex); }
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

    // Headers that belong to the body (HttpContent.Headers); adding them to the request headers throws.
    private static readonly HashSet<string> ContentHeaders =
    [
        with(StringComparer.OrdinalIgnoreCase),
        "Content-Type", "Content-Length", "Content-Encoding", "Content-Language", "Content-Location",
        "Content-MD5", "Content-Range", "Content-Disposition", "Expires", "Last-Modified", "Allow"
    ];

    // Headers whose real-world values often fail .NET's strict parser (product tokens with free text,
    // opaque tokens, non-URI referers); they are added without validation, as before.
    private static readonly HashSet<string> LenientHeaders =
    [
        with(StringComparer.OrdinalIgnoreCase),
        "User-Agent", "Authorization", "Proxy-Authorization", "Cookie", "Referer"
    ];

    /// <summary>
    /// Builds the request. Headers are added with validation (a malformed name or value, or one with a
    /// line break, is rejected and the call returns a failure) except the <see cref="LenientHeaders"/>;
    /// <see cref="ContentHeaders"/> go on the content when there is one.
    /// </summary>
    private static HttpRequestMessage BuildRequest(HttpMethod method, string url,
        HttpContent? content, Dictionary<string, string>? headers)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        if (headers is null) return request;

        foreach (var (key, value) in headers)
        {
            if (ContentHeaders.Contains(key))
            {
                if (content is not null) content.Headers.TryAddWithoutValidation(key, value);
                else request.Headers.TryAddWithoutValidation(key, value);
            }
            else if (LenientHeaders.Contains(key))
                request.Headers.TryAddWithoutValidation(key, value);
            else
                request.Headers.Add(key, value);
        }
        return request;
    }
}
