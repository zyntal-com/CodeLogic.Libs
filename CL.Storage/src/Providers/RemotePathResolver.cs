using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CodeLogic.Core.Results;

namespace CL.Storage.Providers;

internal sealed class RemotePathResolver
{
    private readonly string _remoteRoot;

    public RemotePathResolver(string? root)
    {
        var normalized = StoragePath.Normalize(root ?? string.Empty);
        if (normalized.IsFailure)
            throw new ArgumentException(normalized.Error!.Message, nameof(root));
        Root = normalized.Value!;
        _remoteRoot = Root.Length == 0 ? "/" : "/" + Root;
    }

    public string Root { get; }
    public string RemoteRoot => _remoteRoot;

    public Result<ResolvedRemotePath> Resolve(string path, bool requireNonRoot = false)
    {
        var normalized = StoragePath.Normalize(path);
        if (normalized.IsFailure)
            return Result<ResolvedRemotePath>.Failure(normalized.Error!);
        if (requireNonRoot && normalized.Value!.Length == 0)
            return Result<ResolvedRemotePath>.Failure(StorageErrors.InvalidPath("A non-root storage path is required."));
        var remote = normalized.Value!.Length == 0
            ? _remoteRoot
            : _remoteRoot == "/" ? "/" + normalized.Value : _remoteRoot + "/" + normalized.Value;
        return Result<ResolvedRemotePath>.Success(new ResolvedRemotePath(normalized.Value, remote));
    }

    public string? FromRemotePath(string remotePath)
    {
        var value = remotePath.Replace('\\', '/');
        if (!value.StartsWith('/')) value = "/" + value;
        string relative;
        if (_remoteRoot == "/")
        {
            relative = value.TrimStart('/');
        }
        else if (string.Equals(value, _remoteRoot, StringComparison.Ordinal))
        {
            relative = string.Empty;
        }
        else if (value.StartsWith(_remoteRoot + "/", StringComparison.Ordinal))
        {
            relative = value[(_remoteRoot.Length + 1)..];
        }
        else
        {
            return null;
        }
        var normalized = StoragePath.Normalize(relative);
        return normalized.IsSuccess ? normalized.Value : null;
    }

    public static string Parent(string remotePath)
    {
        var index = remotePath.LastIndexOf('/');
        return index <= 0 ? "/" : remotePath[..index];
    }

    /// <summary>Resolves a relative path (such as a link target) against a remote directory, collapsing <c>.</c> and <c>..</c>.</summary>
    public static string Combine(string directory, string relative)
    {
        var segments = new List<string>();
        foreach (var segment in (directory.TrimEnd('/') + "/" + relative).Split('/'))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }
        return "/" + string.Join('/', segments);
    }
}

internal sealed record ResolvedRemotePath(string StoragePath, string RemotePath);

/// <summary>
/// Maps object-store keys (S3, Azure Blob, Google Cloud Storage, Swift) under a mounted prefix to storage paths. One
/// normaliser for every listing mapper, so a key the library could never address back is skipped the same way by
/// each provider instead of leaking into a listing as a path (and from there into a local download path).
/// </summary>
internal static class ObjectKeyPaths
{
    /// <summary>
    /// The storage path for <paramref name="key"/>, or null when the key is not under <paramref name="prefix"/> or is
    /// unaddressable: a segment of <c>.</c> or <c>..</c>, an empty segment (<c>a//b</c>), a control character, or
    /// anything else <see cref="StoragePath.Normalize"/> would rewrite, since the rewritten path names a different
    /// key. A trailing <c>/</c> (a folder marker) is kept so callers can tell markers from files; the key equal to the
    /// prefix maps to the empty path.
    /// </summary>
    public static string? FromKey(string prefix, string key)
    {
        if (prefix.Length > 0 && !key.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        var relative = key[prefix.Length..];
        var marker = relative.EndsWith('/');
        var body = marker ? relative[..^1] : relative;
        if (body.Length == 0)
            return string.Empty;
        var normalized = StoragePath.Normalize(body);
        return normalized.IsSuccess && string.Equals(normalized.Value, body, StringComparison.Ordinal)
            ? relative
            : null;
    }
}
