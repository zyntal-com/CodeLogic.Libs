using System.Diagnostics.CodeAnalysis;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CL.Storage.Providers.Local;
using CodeLogic.Core.Results;
using WebDAVClient;
using WebDAVClient.Helpers;
using WebDAVClient.Model;

namespace CL.Storage.Providers.WebDav;

public sealed partial class WebDavStorageBackend
{

    /// <summary>
    /// Deletes a collection only while it is verifiably empty. <c>DELETE</c> on a collection always removes everything
    /// in it, so the collection is first locked (<c>LOCK</c>, depth 0, which RFC 4918 says protects its member list:
    /// nobody else can add a member), then listed, and deleted with the lock token only when the listing is empty. A
    /// server without locks, or a backend that does not own its HTTP stack, gets <c>storage.unsupported</c> and the
    /// folder is left as it is; a folder that holds anything (hidden names included) is a conflict.
    /// </summary>
    private async Task<Result> DeleteEmptyCollectionAsync(ResolvedRemotePath resolved, bool ignoreMissing, CancellationToken cancellationToken)
    {
        if (_http is null || _endpoint is null)
            return Result.Failure(StorageErrors.Unsupported(
                "This WebDAV backend does not own its HTTP client, so it cannot lock a collection to delete it only while empty; the folder was left."));
        var uri = ResourceUri(resolved.RemotePath, folder: true);
        string? token;
        using (var request = new HttpRequestMessage(new HttpMethod("LOCK"), uri))
        {
            request.Content = new StringContent(LockBody, System.Text.Encoding.UTF8, "application/xml");
            request.Headers.TryAddWithoutValidation("Depth", "0");
            request.Headers.TryAddWithoutValidation("Timeout", "Second-60");
            AddCustomHeaders(request);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status == 404)
                return ignoreMissing ? Result.Success() : Result.Failure(StorageErrors.NotFound($"WebDAV item '{resolved.StoragePath}' was not found."));
            if (status == 423)
                return Result.Failure(StorageErrors.Conflict("The WebDAV collection is locked by someone else, so it was not deleted."));
            if (status is 400 or 405 or 412 or 501)
                return Result.Failure(StorageErrors.Unsupported(
                    "The WebDAV server does not lock collections, so a non-recursive delete cannot be made safe; the folder was left."));
            if (!response.IsSuccessStatusCode)
                throw new WebDAVException(status, $"WebDAV LOCK failed (Status Code: {status}).");
            token = response.Headers.TryGetValues("Lock-Token", out var values) ? values.FirstOrDefault()?.Trim() : null;
            if (string.IsNullOrEmpty(token))
                return Result.Failure(StorageErrors.Unsupported(
                    "The WebDAV server granted no lock token, so a non-recursive delete cannot be made safe; the folder was left."));
            if (!token.StartsWith('<')) token = $"<{token}>";
        }

        var deleted = false;
        try
        {
            if (await HasMembersAsync(uri, resolved, cancellationToken).ConfigureAwait(false))
                return Result.Failure(StorageErrors.Conflict("The WebDAV directory is not empty."));
            using var request = new HttpRequestMessage(HttpMethod.Delete, uri);
            // A tagged list: an untagged one would also be checked against the parent (Apache mod_dav does), which
            // holds no such lock, and the DELETE would fail with 424.
            request.Headers.TryAddWithoutValidation("If", $"<{uri.AbsoluteUri}> ({token})");
            AddCustomHeaders(request);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.MultiStatus)
                return Result.Failure(StorageErrors.PartialFailure(
                    $"The WebDAV DELETE of '{resolved.StoragePath}' failed for some members (207 Multi-Status).",
                    $"{StorageErrorInfo.HttpStatusKey}=207"));
            if ((int)response.StatusCode is 412 or 423)
                return Result.Failure(StorageErrors.Conflict("The WebDAV collection changed while it was being deleted, so it was kept."));
            if (!response.IsSuccessStatusCode)
                throw new WebDAVException((int)response.StatusCode, $"WebDAV DELETE failed (Status Code: {(int)response.StatusCode}).");
            deleted = true;
            return Result.Success();
        }
        finally
        {
            if (!deleted)
            {
                try
                {
                    using var unlock = new HttpRequestMessage(new HttpMethod("UNLOCK"), uri);
                    unlock.Headers.TryAddWithoutValidation("Lock-Token", token);
                    AddCustomHeaders(unlock);
                    using var _ = await _http.SendAsync(unlock, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception) { /* The lock times out on its own (60 s). */ }
            }
        }
    }

    /// <summary>
    /// Whether a (locked) collection has any member, hidden names included. Asked with a PROPFIND of just the
    /// resource type: the WebDAV client's own listing asks for every property, and on a locked collection the answer
    /// carries the lock's discovery (with its own <c>href</c>), which that client cannot read.
    /// </summary>
    private async Task<bool> HasMembersAsync(Uri collection, ResolvedRemotePath resolved, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), collection)
        {
            Content = new StringContent(
                "<?xml version=\"1.0\" encoding=\"utf-8\"?><D:propfind xmlns:D=\"DAV:\"><D:prop><D:resourcetype/></D:prop></D:propfind>",
                System.Text.Encoding.UTF8, "application/xml")
        };
        request.Headers.TryAddWithoutValidation("Depth", "1");
        AddCustomHeaders(request);
        using var response = await _http!.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new WebDAVException((int)response.StatusCode, $"WebDAV PROPFIND failed (Status Code: {(int)response.StatusCode}).");
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        System.Xml.Linq.XNamespace dav = "DAV:";
        var hrefs = System.Xml.Linq.XDocument.Parse(body).Root?.Elements(dav + "response").Select(element => element.Element(dav + "href")?.Value) ?? [];
        // Anything but the collection itself is a member; an href that cannot be mapped counts as one too.
        return hrefs.Any(href => href is null || FromHref(href, resolved.RemotePath) is not { } path || path != resolved.StoragePath);
    }

    private void AddCustomHeaders(HttpRequestMessage request)
    {
        foreach (var header in _client is Client concrete && concrete.CustomHeaders is { } custom ? custom : [])
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
    }

    /// <summary>Records the certificate each TLS connection is offered; set by the factory.</summary>
    internal ServerIdentityRecorder? Identity { get; init; }

    StorageServerIdentity? IStorageDiagnosticsSource.PresentedIdentity => Identity?.Last;

    StorageSessionPoolStats? IStorageDiagnosticsSource.PoolStats => null;

    /// <summary>Sends <c>OPTIONS</c> to the mounted root and reports the <c>Server</c>, <c>DAV</c>, and <c>Allow</c> headers.</summary>
    async Task<Result<StorageServerDetails>> IStorageDiagnosticsSource.GetServerDetailsAsync(CancellationToken cancellationToken)
    {
        if (_http is null || _endpoint is null)
            return Result<StorageServerDetails>.Failure(StorageErrors.Unsupported("This WebDAV backend does not own its HTTP client."));
        var root = _paths.Resolve(string.Empty);
        if (root.IsFailure) return Result<StorageServerDetails>.Failure(root.Error!);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Options, ResourceUri(root.Value!.RemotePath, folder: true));
            foreach (var header in _client is Client concrete && concrete.CustomHeaders is { } custom ? custom : [])
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new WebDAVException((int)response.StatusCode, $"WebDAV OPTIONS failed (Status Code: {(int)response.StatusCode}).");
            var server = response.Headers.Server.Count > 0 ? response.Headers.Server.ToString() : null;
            var features = new List<string>();
            if (response.Headers.TryGetValues("DAV", out var dav))
                features.AddRange(dav.SelectMany(value => value.Split(',')).Select(value => value.Trim()).Where(value => value.Length > 0).Select(value => $"DAV {value}"));
            features.AddRange(response.Content.Headers.Allow.Select(method => method.ToUpperInvariant()).Order(StringComparer.Ordinal));
            var negotiated = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["http"] = response.Version.ToString()
            };
            return Result<StorageServerDetails>.Success(new StorageServerDetails(server, server?.Split(' ', 2)[0], features.Distinct(StringComparer.Ordinal).ToArray(), negotiated));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageServerDetails>.Failure(Map(error, "Read WebDAV server details")); }
    }

    private Uri ResourceUri(string remotePath, bool folder)
    {
        var basePath = _basePath.TrimEnd('/');
        var encoded = string.Join('/', remotePath.Split('/').Select(Uri.EscapeDataString));
        if (!encoded.StartsWith('/')) encoded = "/" + encoded;
        if (folder && !encoded.EndsWith('/')) encoded += "/";
        return new Uri(_endpoint!, basePath + encoded);
    }

    private static string EnsureTrailingSlash(string path) => path.EndsWith('/') ? path : path + "/";
    private static string NameOf(string path) => path.Split('/')[^1];
    private static bool IsNotFound(WebDAVException error) => HttpStatusOf(error) == 404;

    /// <summary>Reads the HTTP status from a WebDAV failure.</summary>
    /// <remarks>
    /// The client stores the HTTP status in <c>GetHttpCode()</c>; <c>ErrorCode</c> is usually zero. Some
    /// failures only carry the status name in the message ("Status Code: Unauthorized"), so that is the
    /// last resort.
    /// </remarks>
    internal static int HttpStatusOf(WebDAVException error)
    {
        if (error.GetHttpCode() is > 0 and var http) return http;
        if (error.ErrorCode is >= 100 and < 600) return error.ErrorCode;
        var match = StatusInMessage.Match(error.Message ?? string.Empty);
        if (!match.Success) return 0;
        var token = match.Groups[1].Value;
        if (int.TryParse(token, out var numeric)) return numeric;
        return Enum.TryParse<System.Net.HttpStatusCode>(token, ignoreCase: true, out var named) ? (int)named : 0;
    }

    internal static Error Map(Exception exception, string operation)
    {
        if (exception is WebDAVConflictException)
            return StorageErrors.Conflict($"{operation}: WebDAV conflict.");
        if (exception is WebDAVException webDav && HttpStatusOf(webDav) is > 0 and var status)
            return ProviderErrorMapper.FromHttpStatus(status, operation, "WebDAV");
        return ProviderErrorMapper.FromTransport(exception, operation, "WebDAV")
            ?? StorageErrors.ProviderError($"{operation}: WebDAV provider failed.", ProviderErrorMapper.ExceptionDetails(exception));
    }
}
