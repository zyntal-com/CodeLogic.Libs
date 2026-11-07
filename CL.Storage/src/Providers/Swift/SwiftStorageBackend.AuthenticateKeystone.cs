using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CodeLogic.Core.Results;

namespace CL.Storage.Providers.Swift;

public sealed partial class SwiftStorageBackend
{

    private async Task AuthenticateKeystoneAsync(CancellationToken cancellationToken)
    {
        var endpoint = KeystoneTokenUri(_configuration.AuthenticationUrl!);
        var payload = new
        {
            auth = new
            {
                identity = new
                {
                    methods = new[] { "password" },
                    password = new
                    {
                        user = new
                        {
                            name = _configuration.Username,
                            domain = new { name = _configuration.UserDomainName },
                            password = _configuration.Password
                        }
                    }
                },
                scope = new
                {
                    project = new
                    {
                        name = _configuration.ProjectName,
                        domain = new { name = _configuration.ProjectDomainName }
                    }
                }
            }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Keystone authentication failed with HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        if (!response.Headers.TryGetValues("X-Subject-Token", out var tokens))
            throw new InvalidOperationException("Keystone did not return X-Subject-Token.");
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var tokenNode = document.RootElement.GetProperty("token");
        var expires = tokenNode.TryGetProperty("expires_at", out var expiresNode) &&
            DateTimeOffset.TryParse(expiresNode.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : DateTimeOffset.UtcNow.AddHours(1);
        var storageUrl = _configuration.StorageUrl;
        if (string.IsNullOrWhiteSpace(storageUrl) && FindObjectStoreEndpoint(tokenNode) is { } catalogUrl)
            storageUrl = CheckedStorageUrl(catalogUrl, "Keystone");
        _token = tokens.First();
        _storageUrl = storageUrl?.TrimEnd('/') ??
            throw new InvalidOperationException("Keystone catalog did not contain a matching object-store endpoint.");
        _tokenExpiresAt = expires;
    }

    /// <summary>
    /// The storage URL an authentication reply returned, after the same scheme check the configured URLs get:
    /// the token is sent to every request on it, so an <c>http://</c> endpoint from the server is refused unless
    /// <see cref="SwiftConnectionConfig.AllowInsecureHttp"/> is on (the failure maps to <c>storage.provider_error</c>).
    /// </summary>
    private string CheckedStorageUrl(string url, string source) =>
        _configuration.IsAllowedUrl(url)
            ? url
            : throw new InvalidOperationException(
                $"{source} returned a storage URL whose scheme is not allowed for this connection (set AllowInsecureHttp to accept http).");

    private string? FindObjectStoreEndpoint(JsonElement tokenNode)
    {
        if (!tokenNode.TryGetProperty("catalog", out var catalog)) return null;
        foreach (var service in catalog.EnumerateArray())
        {
            if (!service.TryGetProperty("type", out var type) || type.GetString() != "object-store") continue;
            foreach (var endpoint in service.GetProperty("endpoints").EnumerateArray())
            {
                var endpointInterface = endpoint.TryGetProperty("interface", out var interfaceNode) ? interfaceNode.GetString() : null;
                var region = endpoint.TryGetProperty("region", out var regionNode) ? regionNode.GetString() : null;
                if (!string.Equals(endpointInterface, _configuration.EndpointInterface, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrWhiteSpace(_configuration.Region) &&
                    !string.Equals(region, _configuration.Region, StringComparison.OrdinalIgnoreCase)) continue;
                if (endpoint.TryGetProperty("url", out var url)) return url.GetString();
            }
        }
        return null;
    }

    private void InvalidateAuthentication()
    {
        _token = null;
        if (_configuration.AuthenticationMode != SwiftAuthenticationMode.StaticToken) _storageUrl = null;
        _tokenExpiresAt = default;
    }

    private async Task<Result<StorageItem>> GetDirectoryInfoAsync(string path, CancellationToken cancellationToken)
    {
        var page = await ListProviderPageAsync(DirectoryPrefix(path), null, 1, null, cancellationToken).ConfigureAwait(false);
        return page.IsFailure
            ? Result<StorageItem>.Failure(page.Error!)
            : page.Value!.Items.Count > 0
                ? Result<StorageItem>.Success(DirectoryItem(path))
                : Result<StorageItem>.Failure(StorageErrors.NotFound($"Swift item '{path}' was not found."));
    }

    private async Task<Result<SwiftPage>> ListProviderPageAsync(
        string prefix,
        string? delimiter,
        int limit,
        string? marker,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() =>
        {
            var parameters = new List<string>
            {
                "format=json",
                "prefix=" + Uri.EscapeDataString(prefix),
                "limit=" + limit.ToString(CultureInfo.InvariantCulture)
            };
            if (delimiter is not null) parameters.Add("delimiter=" + Uri.EscapeDataString(delimiter));
            if (!string.IsNullOrWhiteSpace(marker)) parameters.Add("marker=" + Uri.EscapeDataString(marker));
            return new HttpRequestMessage(HttpMethod.Get, ContainerUri() + "?" + string.Join("&", parameters));
        }, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return Result<SwiftPage>.Failure(FromStatus(response, "List Swift objects"));
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var items = new List<SwiftListItem>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.TryGetProperty("subdir", out var subdirectory))
            {
                items.Add(new SwiftListItem(null, subdirectory.GetString(), null, null, null, null));
                continue;
            }
            var name = element.GetProperty("name").GetString();
            long? bytes = element.TryGetProperty("bytes", out var bytesNode) ? bytesNode.GetInt64() : null;
            DateTimeOffset? lastModified = element.TryGetProperty("last_modified", out var modifiedNode) &&
                DateTimeOffset.TryParse(modifiedNode.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var modified)
                    ? modified
                    : null;
            items.Add(new SwiftListItem(
                name,
                null,
                bytes,
                lastModified,
                element.TryGetProperty("content_type", out var contentType) ? contentType.GetString() : null,
                element.TryGetProperty("hash", out var hash) ? hash.GetString() : null));
        }
        var last = items.LastOrDefault();
        var next = items.Count == limit ? last?.Subdirectory ?? last?.Name : null;
        return Result<SwiftPage>.Success(new SwiftPage(items, next));
    }

    private async Task<Result<IReadOnlyList<string>>> ListAllNamesAsync(string prefix, CancellationToken cancellationToken)
    {
        var names = new List<string>();
        string? marker = null;
        do
        {
            var page = await ListProviderPageAsync(prefix, null, 1000, marker, cancellationToken).ConfigureAwait(false);
            if (page.IsFailure) return Result<IReadOnlyList<string>>.Failure(page.Error!);
            names.AddRange(page.Value!.Items.Where(item => item.Name is not null).Select(item => item.Name!));
            marker = page.Value.NextMarker;
        } while (!string.IsNullOrWhiteSpace(marker));
        return Result<IReadOnlyList<string>>.Success(names.AsReadOnly());
    }

    private async Task<Result> DeleteObjectAsync(
        string key,
        CancellationToken cancellationToken,
        bool ignoreMissing = false)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Delete, ObjectUri(key)),
            cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode || ignoreMissing && response.StatusCode == HttpStatusCode.NotFound)
            return Result.Success();
        return Result.Failure(FromStatus(response, "Delete Swift object"));
    }

    private Uri ContainerUri() => new($"{_storageUrl}/{Uri.EscapeDataString(_configuration.Container)}", UriKind.Absolute);
    private Uri ObjectUri(string key, string? versionId = null) =>
        new($"{ContainerUri()}/{EncodeObjectPath(key)}{(versionId is null ? string.Empty : "?version-id=" + Uri.EscapeDataString(versionId))}", UriKind.Absolute);
    private static string EncodeObjectPath(string key) => string.Join("/", key.Split('/').Select(Uri.EscapeDataString));
    private static Uri KeystoneTokenUri(string authenticationUrl)
    {
        var value = authenticationUrl.TrimEnd('/');
        if (value.EndsWith("/auth/tokens", StringComparison.OrdinalIgnoreCase)) return new Uri(value);
        if (!value.EndsWith("/v3", StringComparison.OrdinalIgnoreCase)) value += "/v3";
        return new Uri(value + "/auth/tokens");
    }

    private string ToKey(string path) => _prefix + path;
    private string DirectoryPrefix(string path) => path.Length == 0 ? _prefix : ToKey(path).TrimEnd('/') + "/";
    /// <summary>The storage path of an object name under the mounted prefix, or null for a name the library cannot address (see <see cref="ObjectKeyPaths"/>).</summary>
    private string? FromKey(string key) => ObjectKeyPaths.FromKey(_prefix, key);
    private Result<string> Normalize(string path) => StoragePath.Normalize(path);
    private Result<string> NormalizeRequired(string path)
    {
        var normalized = Normalize(path);
        return normalized.IsFailure || normalized.Value!.Length > 0
            ? normalized
            : Result<string>.Failure(StorageErrors.InvalidPath("A non-root storage path is required."));
    }

    private static StorageItem ToItem(string path, HttpResponseMessage response)
    {
        response.Content.Headers.TryGetValues("Content-Type", out var contentTypes);
        response.Headers.TryGetValues("X-Object-Version-Id", out var versionIds);
        var metadata = response.Headers
            .Where(header => header.Key.StartsWith("X-Object-Meta-", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                header => header.Key["X-Object-Meta-".Length..],
                header => string.Join(",", header.Value),
                StringComparer.OrdinalIgnoreCase);
        return new StorageItem
        {
            Path = path,
            Name = NameOf(path),
            ItemType = path.EndsWith('/') ? StorageItemType.Directory : StorageItemType.File,
            Size = path.EndsWith('/') ? null : response.Content.Headers.ContentLength,
            LastModified = response.Content.Headers.LastModified,
            ContentType = contentTypes?.FirstOrDefault(),
            ETag = RawETag(response),
            VersionId = versionIds?.FirstOrDefault(),
            Metadata = metadata
        };
    }

    /// <summary>
    /// Swift sends its ETag unquoted, which the typed <c>ETag</c> header parser rejects as invalid,
    /// so the header is read raw. Quotes are stripped when a proxy or middleware adds them.
    /// </summary>
    internal static string? RawETag(HttpResponseMessage response) =>
        response.Headers.TryGetValues("ETag", out var values) && values.FirstOrDefault() is { Length: > 0 } raw
            ? raw.Trim().Trim('"')
            : null;

    private static string QuoteETag(string etag) =>
        etag.StartsWith('"') && etag.EndsWith('"') ? etag : $"\"{etag}\"";
}
