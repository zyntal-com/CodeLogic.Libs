using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CodeLogic.Core.Results;

namespace CL.Storage.Providers.Local;

public sealed partial class LocalStorageBackend
{

    /// <inheritdoc />
    public string ConnectionId { get; }

    /// <summary>Identifies the listing snapshots continuation tokens refer to; settings-based so tokens outlive a registration.</summary>
    internal string? ListingScope { get; init; }
    /// <inheritdoc />
    public StorageProvider Provider => StorageProvider.Local;
    /// <inheritdoc />
    public string Root => _paths.Root;
    /// <inheritdoc />
    /// <remarks>
    /// <see cref="StorageFeature.CaseInsensitivePaths"/> is decided by the root's file system, probed when the
    /// connection is created (see <see cref="DetectCaseInsensitive"/>).
    /// </remarks>
    public StorageCapabilities Capabilities => _capabilities;

    /// <summary>
    /// Whether names under <paramref name="root"/> compare without regard to case. It depends on the file system and
    /// the mount, not the operating system (APFS and ext4 volumes can be either, a Windows folder can be made
    /// case-sensitive, a Linux mount of NTFS or SMB is not), so it is probed: an entry of the root, named with the
    /// case of its ASCII letters swapped (see <see cref="SwapCase"/>), either resolves or not. A root with no such
    /// entry, or one that cannot be read,
    /// gets the operating system's default (Windows and macOS insensitive, others sensitive). Subfolders are assumed
    /// to behave like the root.
    /// </summary>
    internal static bool DetectCaseInsensitive(string root)
    {
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(root).Take(64))
            {
                var name = Path.GetFileName(entry);
                var swapped = SwapCase(name);
                if (string.Equals(swapped, name, StringComparison.Ordinal))
                    continue;
                if (!Path.Exists(Path.Combine(root, swapped)))
                    return false;
                // On a case-sensitive file system the swapped name may be another entry: it resolves, but not to this one.
                var literal = Directory.EnumerateFileSystemEntries(root, swapped, new EnumerationOptions { MatchCasing = MatchCasing.CaseSensitive, RecurseSubdirectories = false });
                if (literal.Any(found => string.Equals(Path.GetFileName(found), swapped, StringComparison.Ordinal)))
                    continue;
                return true;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
    }

    /// <summary>
    /// The name with the case of its ASCII letters swapped. Other letters are left alone: file systems fold case by
    /// their own tables, which differ from .NET's invariant mapping outside ASCII (NTFS does not map Turkish dotless
    /// <c>ı</c> to <c>I</c>, nor Georgian as .NET does), so only ASCII tells how the file system compares names. A name
    /// without ASCII letters comes back unchanged and is skipped by the probe.
    /// </summary>
    internal static string SwapCase(string name) =>
        string.Create(name.Length, name, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                span[i] = char.IsAsciiLetterUpper(c) ? (char)(c + 32) : char.IsAsciiLetterLower(c) ? (char)(c - 32) : c;
            }
        });

    /// <inheritdoc />
    public Task<Result<StorageItem>> GetInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure)
            return Task.FromResult(Result<StorageItem>.Failure(resolved.Error!));
        try
        {
            var item = CreateItem(resolved.Value!.StoragePath, resolved.Value.FullPath);
            return Task.FromResult(item is null
                ? Result<StorageItem>.Failure(StorageErrors.NotFound($"Storage item '{resolved.Value.StoragePath}' was not found."))
                : Result<StorageItem>.Success(item));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            return Task.FromResult(Result<StorageItem>.Failure(StorageErrors.FromException(error, "Get item info")));
        }
    }

    /// <inheritdoc />
    public Task<Result<bool>> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure)
            return Task.FromResult(Result<bool>.Failure(resolved.Error!));
        try
        {
            _ = File.GetAttributes(resolved.Value!.FullPath);
            return Task.FromResult(Result<bool>.Success(true));
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return Task.FromResult(Result<bool>.Success(false));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            return Task.FromResult(Result<bool>.Failure(StorageErrors.FromException(error, "Check item existence")));
        }
    }

    /// <inheritdoc />
    public async Task<Result<StoragePage>> ListAsync(string path, StorageListOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new StorageListOptions();
        var validation = options.Validate();
        if (validation.IsFailure)
            return Result<StoragePage>.Failure(validation.Error!);
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure)
            return Result<StoragePage>.Failure(resolved.Error!);

        try
        {
            if (!Directory.Exists(resolved.Value!.FullPath))
                return Result<StoragePage>.Failure(StorageErrors.NotFound($"Directory '{resolved.Value.StoragePath}' was not found."));

            // Directory enumeration runs once per listing pass instead of once per page.
            var target = resolved.Value!;
            return await ProviderPaging.CreateAsync(
                ProviderPaging.Scope(ListingScope ?? ConnectionId, target.StoragePath, options.Recursive),
                options,
                token => Task.FromResult(Result<IEnumerable<StorageItem>>.Success(
                    EnumerateItems(target, options.Recursive, token).ToArray().AsEnumerable())),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            return Result<StoragePage>.Failure(StorageErrors.FromException(error, "List directory"));
        }
    }

    /// <inheritdoc />
    public Task<Result> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure)
            return Task.FromResult(Result.Failure(resolved.Error!));
        try
        {
            Directory.CreateDirectory(resolved.Value!.FullPath);
            return Task.FromResult(Result.Success());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            return Task.FromResult(Result.Failure(StorageErrors.FromException(error, "Create directory")));
        }
    }

    /// <inheritdoc />
    public async Task<Result<StorageItem>> UploadAsync(
        string path,
        Stream source,
        StorageUploadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(source);
        if (StorageTransferPipeline.Applies(this, options))
            return await StorageTransferPipeline.UploadAsync(this, path, source, options, cancellationToken).ConfigureAwait(false);
        options ??= new StorageUploadOptions();
        var validation = options.Validate();
        if (validation.IsFailure)
            return Result<StorageItem>.Failure(validation.Error!);
        if (options.Condition is { IsEmpty: false })
            return Result<StorageItem>.Failure(StorageErrors.Unsupported(
                "The local provider does not support atomic ETag or version upload conditions."));
        if (options.Metadata.Count > 0)
            return Result<StorageItem>.Failure(StorageErrors.Unsupported(
                "Local storage does not persist provider user metadata."));
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure)
            return Result<StorageItem>.Failure(resolved.Error!);
        if (resolved.Value!.StoragePath.Length == 0)
            return Result<StorageItem>.Failure(StorageErrors.InvalidPath("A file path is required for upload."));

        string? stagingPath = null;
        try
        {
            var parent = Path.GetDirectoryName(resolved.Value.FullPath)!;
            if (options.CreateParents)
                Directory.CreateDirectory(parent);
            else if (!Directory.Exists(parent))
                return Result<StorageItem>.Failure(StorageErrors.NotFound("The destination parent directory was not found."));

            if (Directory.Exists(resolved.Value.FullPath))
                return Result<StorageItem>.Failure(StorageErrors.Conflict("The upload destination is a directory."));
            if (!options.Overwrite && File.Exists(resolved.Value.FullPath))
                return Result<StorageItem>.Failure(StorageErrors.Conflict("The upload destination already exists."));

            stagingPath = CreateStagingPath(parent, "upload");
            var destination = new FileStream(
                stagingPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81_920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (destination.ConfigureAwait(false))
            {
                await source.CopyToAsync(destination, 81_920, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(stagingPath, resolved.Value.FullPath, options.Overwrite);
            stagingPath = null;
            return await GetInfoAsync(resolved.Value.StoragePath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException) when (!options.Overwrite && File.Exists(resolved.Value.FullPath))
        {
            return Result<StorageItem>.Failure(StorageErrors.Conflict("The upload destination already exists."));
        }
        catch (Exception error)
        {
            return Result<StorageItem>.Failure(StorageErrors.FromException(error, "Upload file"));
        }
        finally
        {
            TryDeleteStagingFile(stagingPath);
        }
    }
}
