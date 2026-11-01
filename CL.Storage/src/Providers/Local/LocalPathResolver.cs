using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CodeLogic.Core.Results;

namespace CL.Storage.Providers.Local;

internal sealed class LocalPathResolver
{
    private readonly bool _followLinks;
    private readonly StringComparison _comparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public LocalPathResolver(string rootPath, bool followLinks)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("A local root path is required.", nameof(rootPath));
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        _followLinks = followLinks;
    }

    public string Root { get; }

    /// <summary>
    /// Whether a path is a link: a symbolic link or a junction (a reparse point with a link target). Other reparse
    /// points (OneDrive and other cloud-file placeholders, deduplicated files, app execution aliases) are ordinary
    /// files and folders. A reparse point whose data cannot be read is treated as a link.
    /// </summary>
    internal static bool IsLink(string fullPath, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) == 0)
            return false;
        try
        {
            FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(fullPath) : new FileInfo(fullPath);
            return info.LinkTarget is not null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    public Result<ResolvedLocalPath> Resolve(string path) => Resolve(path, allowFinalLink: false);

    /// <summary>
    /// Resolves a path whose last segment may itself be a link, without following it. Used to create,
    /// read, or change a link while every parent directory still gets the usual link checks.
    /// </summary>
    public Result<ResolvedLocalPath> ResolveLink(string path) => Resolve(path, allowFinalLink: true);

    private Result<ResolvedLocalPath> Resolve(string path, bool allowFinalLink)
    {
        var normalized = StoragePath.Normalize(path);
        if (normalized.IsFailure)
            return Result<ResolvedLocalPath>.Failure(normalized.Error!);

        // On Windows a colon is never part of a file name: "file.txt:name" addresses an NTFS alternate data stream
        // (which passes the containment check) and "C:name" is drive-relative, resolved against the process's
        // current directory on that drive instead of the root.
        if (OperatingSystem.IsWindows() && normalized.Value!.Contains(':'))
            return Result<ResolvedLocalPath>.Failure(StorageErrors.InvalidPath(
                "A local path cannot contain ':' on Windows (alternate data streams and drive-relative paths are not allowed)."));

        // "CON", "nul.txt", "COM1.log" and the like open a device instead of a file under the root.
        if (OperatingSystem.IsWindows() && normalized.Value!.Split('/').Any(StoragePath.IsWindowsReservedName))
            return Result<ResolvedLocalPath>.Failure(StorageErrors.InvalidPath(
                "A local path cannot use a Windows reserved device name (CON, PRN, AUX, NUL, COM0-9, LPT0-9)."));

        try
        {
            var fullPath = normalized.Value!.Length == 0
                ? Root
                : Path.GetFullPath(Path.Combine(Root, normalized.Value.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsContained(fullPath))
                return Result<ResolvedLocalPath>.Failure(StorageErrors.InvalidPath("The path escapes the configured root."));

            var current = Root;
            var segments = normalized.Value.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < segments.Length; index++)
            {
                if (allowFinalLink && index == segments.Length - 1)
                    break;
                current = Path.Combine(current, segments[index]);
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(current);
                }
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
                {
                    break;
                }

                if (!IsLink(current, attributes))
                    continue;
                if (!_followLinks)
                    return Result<ResolvedLocalPath>.Failure(StorageErrors.InvalidPath("Links and reparse points are disabled for this connection."));

                FileSystemInfo info = (attributes & FileAttributes.Directory) != 0
                    ? new DirectoryInfo(current)
                    : new FileInfo(current);
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is null || !IsContained(Path.GetFullPath(target.FullName)))
                    return Result<ResolvedLocalPath>.Failure(StorageErrors.InvalidPath("A link resolves outside the configured root."));
            }

            return Result<ResolvedLocalPath>.Success(new ResolvedLocalPath(normalized.Value, fullPath));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            return Result<ResolvedLocalPath>.Failure(StorageErrors.FromException(error, "Resolve path"));
        }
    }

    /// <summary>Maps a contained full path back to a storage path, or returns null when it lies outside the root.</summary>
    public string? ToStoragePath(string fullPath)
    {
        var full = Path.GetFullPath(fullPath);
        if (!IsContained(full)) return null;
        var relative = Path.GetRelativePath(Root, full).Replace(Path.DirectorySeparatorChar, '/');
        return relative == "." ? string.Empty : relative;
    }

    public bool IsContained(string fullPath)
    {
        if (string.Equals(fullPath, Root, _comparison))
            return true;
        var prefix = Root.EndsWith(Path.DirectorySeparatorChar) ? Root : Root + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, _comparison);
    }
}

internal sealed record ResolvedLocalPath(string StoragePath, string FullPath);
