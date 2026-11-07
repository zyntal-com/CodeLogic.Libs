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

    private async Task<Result> CheckHealthCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var options = await _client.GetServerOptions(_paths.RemoteRoot, cancellationToken).ConfigureAwait(false);
            return options.IsWebDavServer
                ? Result.Success()
                : Result.Failure(StorageErrors.Unavailable("The endpoint did not identify itself as a WebDAV server."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Check WebDAV health")); }
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyDictionary<string, string>>> GetMetadataAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = await GetInfoCoreAsync(path, cancellationToken).ConfigureAwait(false);
        return info.IsSuccess
            ? Result<IReadOnlyDictionary<string, string>>.Success(info.Value!.Metadata)
            : Result<IReadOnlyDictionary<string, string>>.Failure(info.Error!);
    }

    /// <inheritdoc />
    public Task<Result<StorageItem>> SetMetadataAsync(
        string path,
        IReadOnlyDictionary<string, string> metadata,
        StorageMetadataUpdateOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(metadata);
        options ??= new StorageMetadataUpdateOptions();
        var validation = options.Validate(metadata);
        if (validation.IsFailure)
            return Task.FromResult(Result<StorageItem>.Failure(validation.Error!));
        return Task.FromResult(Result<StorageItem>.Failure(StorageErrors.Unsupported(
            "This WebDAV adapter exposes discovered properties as read-only metadata; use the native client for server-specific PROPPATCH operations.")));
    }

    /// <inheritdoc />
    public bool TryGetNativeClient<TClient>([NotNullWhen(true)] out TClient? client) where TClient : class
    {
        client = _client as TClient;
        return client is not null;
    }

    /// <inheritdoc />
    public Task<Result<NativeConnectionLease<TClient>>> OpenNativeConnectionAsync<TClient>(CancellationToken cancellationToken = default) where TClient : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_client is not TClient typed)
            return Task.FromResult(Result<NativeConnectionLease<TClient>>.Failure(StorageErrors.Unsupported($"WebDAV does not expose native type '{typeof(TClient).FullName}'.")));
        return Task.FromResult(Result<NativeConnectionLease<TClient>>.Success(
            new NativeConnectionLease<TClient>(typed, _ => ValueTask.CompletedTask)));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_ownsClient && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _client.Dispose();
            _http?.Dispose();
            Owned?.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    private async Task<Item?> FindItemAsync(ResolvedRemotePath resolved, CancellationToken cancellationToken)
    {
        var parent = RemotePathResolver.Parent(resolved.RemotePath);
        var items = (await _client.List(parent, depth: 1, cancellationToken).ConfigureAwait(false)).ToList();
        var exact = items.FirstOrDefault(item => string.Equals(FromHref(item.Href, parent), resolved.StoragePath, StringComparison.Ordinal));
        if (exact is not null) return exact;
        // The server may compare names without regard to case (IIS, a Windows or macOS share) and answer in its own
        // spelling: when one entry differs only in case, the requested spelling is asked for itself.
        var folded = items.Where(item => string.Equals(FromHref(item.Href, parent), resolved.StoragePath, StringComparison.OrdinalIgnoreCase)).ToList();
        if (folded.Count != 1) return null;
        try
        {
            return folded[0].IsCollection
                ? await _client.GetFolder(resolved.RemotePath, cancellationToken).ConfigureAwait(false)
                : await _client.GetFile(resolved.RemotePath, cancellationToken).ConfigureAwait(false);
        }
        catch (WebDAVException error) when (IsNotFound(error))
        {
            return null;
        }
    }

    private async Task<List<StorageItem>> CollectListingAsync(ResolvedRemotePath root, bool recursive, CancellationToken cancellationToken)
    {
        var result = new List<StorageItem>();
        var pending = new Queue<string>();
        pending.Enqueue(root.RemotePath);
        while (pending.Count > 0)
        {
            var directory = pending.Dequeue();
            var items = await _client.List(directory, depth: 1, cancellationToken).ConfigureAwait(false);
            foreach (var item in items)
            {
                var relative = FromHref(item.Href, directory);
                if (relative is null || relative.Length == 0 || relative == _paths.FromRemotePath(directory)) continue;
                result.Add(ToItem(relative, item));
                if (recursive && item.IsCollection) pending.Enqueue(ToRemotePath(relative));
            }
        }
        return result.GroupBy(item => item.Path, StringComparer.Ordinal).Select(group => group.First()).ToList();
    }

    private async Task EnsureDirectoryAsync(string remotePath, CancellationToken cancellationToken)
    {
        var current = string.Empty;
        foreach (var segment in remotePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + segment;
            var storagePath = _paths.FromRemotePath(current);
            if (storagePath is null) continue;
            var exists = await ExistsCoreAsync(storagePath, cancellationToken).ConfigureAwait(false);
            if (exists.IsFailure) throw new WebDAVException(exists.Error!.Message);
            if (exists.Value) continue;
            var parent = RemotePathResolver.Parent(current);
            var created = await _client.CreateDir(EnsureTrailingSlash(parent), segment, cancellationToken).ConfigureAwait(false);
            if (!created) throw new WebDAVException($"Could not create WebDAV directory '{current}'.");
        }
    }

    /// <summary>
    /// Maps a PROPFIND href to a storage path. <paramref name="listed"/> is the remote folder that was listed: a
    /// server that compares names without regard to case answers with its own spelling of that folder
    /// (<c>/Docs/</c> for a request of <c>/docs/</c>), which is taken back to the spelling asked for, so the entries
    /// stay under the requested root instead of being dropped as outside it.
    /// </summary>
    private string? FromHref(string href, string? listed = null)
    {
        // Only http(s) hrefs are absolute: on Unix, Uri also parses "/dir/a%20b" as a file path and keeps
        // "%20" literal, so names needing escapes would never match.
        var value = Uri.TryCreate(href, UriKind.Absolute, out var absolute) && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps)
            ? absolute.AbsolutePath
            : href;
        value = Uri.UnescapeDataString(value).Replace('\\', '/');
        if (!value.StartsWith('/')) value = "/" + value;
        if (_basePath != "/" && value.StartsWith(_basePath, StringComparison.OrdinalIgnoreCase))
            value = "/" + value[_basePath.Length..].TrimStart('/');
        if (listed is not null)
        {
            var anchor = "/" + listed.Trim('/');
            if (anchor.Length > 1 && value.StartsWith(anchor, StringComparison.OrdinalIgnoreCase) &&
                !value.StartsWith(anchor, StringComparison.Ordinal) &&
                (value.Length == anchor.Length || value[anchor.Length] == '/'))
                value = anchor + value[anchor.Length..];
        }
        return _paths.FromRemotePath(value.TrimEnd('/'));
    }

    private string ToRemotePath(string storagePath) => _paths.Resolve(storagePath).Value!.RemotePath;

    private static async Task SkipAsync(Stream stream, long count, CancellationToken cancellationToken)
    {
        var buffer = new byte[81_920];
        while (count > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            count -= read;
        }
    }

    private static StorageItem ToItem(string path, Item item) => new()
    {
        Path = path,
        Name = NameOf(path),
        ItemType = item.IsCollection ? StorageItemType.Directory : StorageItemType.File,
        Size = item.IsCollection ? null : item.ContentLength,
        LastModified = item.LastModified.HasValue ? new DateTimeOffset(item.LastModified.Value.ToUniversalTime()) : null,
        ContentType = item.ContentType,
        ETag = item.Etag,
        Metadata = item.FoundProperties?.ToDictionary(pair => $"{pair.Key.Namespace}{pair.Key.LocalName}", pair => pair.Value, StringComparer.Ordinal) ??
            []
    };

    private static StorageItem DirectoryItem(string path) => new()
    {
        Path = path,
        Name = path.Length == 0 ? string.Empty : NameOf(path),
        ItemType = StorageItemType.Directory
    };

    private static string NormalizeBasePath(string? path)
    {
        var value = string.IsNullOrWhiteSpace(path) ? "/" : path.Replace('\\', '/');
        if (!value.StartsWith('/')) value = "/" + value;
        if (!value.EndsWith('/')) value += "/";
        return value;
    }

    /// <summary>
    /// Issues a WebDAV MOVE or COPY. Returns false when the destination exists and overwrite is off.
    /// </summary>
    /// <remarks>
    /// The WebDAV client library does not dispose the responses of its move and copy calls, so each one
    /// strands a pooled connection until garbage collection; with a connection limit the next request
    /// blocks forever. When this backend owns the HTTP stack it sends these requests itself.
    /// </remarks>
    private async Task<DavTransfer> TransferAsync(string method, string sourceRemotePath, string destinationRemotePath, bool overwrite, bool folder, CancellationToken cancellationToken)
    {
        if (_http is null || _endpoint is null)
        {
            // The client library cannot report a 207 Multi-Status; only a backend that owns its HTTP stack can.
            var done = (method, folder) switch
            {
                ("MOVE", true) => await _client.MoveFolder(sourceRemotePath, destinationRemotePath, overwrite, null, null, cancellationToken).ConfigureAwait(false),
                ("MOVE", false) => await _client.MoveFile(sourceRemotePath, destinationRemotePath, overwrite, null, null, cancellationToken).ConfigureAwait(false),
                (_, true) => await _client.CopyFolder(sourceRemotePath, destinationRemotePath, overwrite, null, cancellationToken).ConfigureAwait(false),
                _ => await _client.CopyFile(sourceRemotePath, destinationRemotePath, overwrite, null, cancellationToken).ConfigureAwait(false)
            };
            return done ? DavTransfer.Done : DavTransfer.DestinationExists;
        }

        using var request = new HttpRequestMessage(new HttpMethod(method), ResourceUri(sourceRemotePath, folder));
        request.Headers.TryAddWithoutValidation("Destination", ResourceUri(destinationRemotePath, folder).AbsoluteUri);
        request.Headers.TryAddWithoutValidation("Overwrite", overwrite ? "T" : "F");
        if (folder) request.Headers.TryAddWithoutValidation("Depth", "infinity");
        foreach (var header in _client is Client concrete && concrete.CustomHeaders is { } custom ? custom : [])
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        // 207 is a 2xx status, but for COPY and MOVE it lists the members that failed.
        if (response.StatusCode == System.Net.HttpStatusCode.MultiStatus)
            return DavTransfer.Partial;
        if (response.IsSuccessStatusCode)
            return DavTransfer.Done;
        if (response.StatusCode == System.Net.HttpStatusCode.PreconditionFailed && !overwrite)
            return DavTransfer.DestinationExists;
        throw new WebDAVException((int)response.StatusCode, $"WebDAV {method} failed (Status Code: {(int)response.StatusCode}).");
    }
}
