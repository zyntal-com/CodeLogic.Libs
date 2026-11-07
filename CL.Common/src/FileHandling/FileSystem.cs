using CodeLogic.Core.Results;

namespace CL.Common.FileHandling;

/// <summary>
/// Provides safe file system operations that return <see cref="Result{T}"/>
/// instead of throwing exceptions. All async methods are non-blocking.
/// </summary>
public static class FileSystem
{
    /// <summary>Reads all text from a file asynchronously.</summary>
    public static async Task<Result<string>> ReadAllTextAsync(string path)
    {
        try
        {
            if (!File.Exists(path))
                return Error.NotFound(ErrorCode.FileNotFound, $"File not found: {path}");
            return await File.ReadAllTextAsync(path).ConfigureAwait(false);
        }
        catch (Exception ex) { return Error.Internal(ErrorCode.FileReadFailed, $"Failed to read '{path}'", ex.Message); }
    }

    /// <summary>Writes text to a file asynchronously, creating the file and any missing directories.</summary>
    public static async Task<Result> WriteAllTextAsync(string path, string content)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(path, content).ConfigureAwait(false);
            return Result.Success();
        }
        catch (Exception ex) { return Error.Internal(ErrorCode.FileWriteFailed, $"Failed to write '{path}'", ex.Message); }
    }

    /// <summary>Reads all bytes from a file asynchronously.</summary>
    public static async Task<Result<byte[]>> ReadAllBytesAsync(string path)
    {
        try
        {
            if (!File.Exists(path))
                return Error.NotFound(ErrorCode.FileNotFound, $"File not found: {path}");
            return await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        }
        catch (Exception ex) { return Error.Internal(ErrorCode.FileReadFailed, $"Failed to read '{path}'", ex.Message); }
    }

    /// <summary>Writes bytes to a file asynchronously, creating any missing directories.</summary>
    public static async Task<Result> WriteAllBytesAsync(string path, byte[] data)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(path, data).ConfigureAwait(false);
            return Result.Success();
        }
        catch (Exception ex) { return Error.Internal(ErrorCode.FileWriteFailed, $"Failed to write '{path}'", ex.Message); }
    }

    /// <summary>Copies a file asynchronously.</summary>
    /// <param name="source">The source file path.</param>
    /// <param name="dest">The destination file path.</param>
    /// <param name="overwrite">When true, overwrites the destination if it exists.</param>
    public static async Task<Result> CopyAsync(string source, string dest, bool overwrite = false)
    {
        try
        {
            if (!File.Exists(source))
                return Error.NotFound(ErrorCode.FileNotFound, $"Source file not found: {source}");
            if (!overwrite && File.Exists(dest))
                return Error.Conflict(ErrorCode.AlreadyExists, $"Destination already exists: {dest}");

            var dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var src = File.OpenRead(source);
            await using (src.ConfigureAwait(false))
            {
                var dst = File.Open(dest, overwrite ? FileMode.Create : FileMode.CreateNew);
                await using (dst.ConfigureAwait(false))
                    await src.CopyToAsync(dst).ConfigureAwait(false);
            }
            return Result.Success();
        }
        catch (Exception ex) { return Error.Internal(ErrorCode.Internal, $"Failed to copy '{source}' to '{dest}'", ex.Message); }
    }

    /// <summary>Moves a file, creating any missing destination directories.</summary>
    public static Task<Result> MoveAsync(string source, string dest)
    {
        try
        {
            if (!File.Exists(source))
                return Task.FromResult<Result>(Error.NotFound(ErrorCode.FileNotFound, $"Source not found: {source}"));
            var dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Move(source, dest, overwrite: false);
            return Task.FromResult(Result.Success());
        }
        catch (Exception ex) { return Task.FromResult<Result>(Error.Internal(ErrorCode.Internal, $"Failed to move '{source}'", ex.Message)); }
    }

    /// <summary>Deletes a file. Returns success even if the file does not exist.</summary>
    public static Result DeleteFile(string path)
    {
        try { File.Delete(path); return Result.Success(); }
        catch (Exception ex) { return Error.Internal(ErrorCode.Internal, $"Failed to delete '{path}'", ex.Message); }
    }

    /// <summary>Creates a directory and all intermediate directories. Succeeds if already exists.</summary>
    public static Result CreateDirectory(string path)
    {
        try { Directory.CreateDirectory(path); return Result.Success(); }
        catch (Exception ex) { return Error.Internal(ErrorCode.Internal, $"Failed to create directory '{path}'", ex.Message); }
    }

    /// <summary>Lists all files in a directory matching the given search pattern.</summary>
    public static Task<Result<string[]>> GetFilesAsync(string directory, string pattern = "*")
    {
        try
        {
            if (!Directory.Exists(directory))
                return Task.FromResult<Result<string[]>>(Error.NotFound(ErrorCode.FileNotFound, $"Directory not found: {directory}"));
            var files = Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly);
            return Task.FromResult<Result<string[]>>(files);
        }
        catch (Exception ex) { return Task.FromResult<Result<string[]>>(Error.Internal(ErrorCode.Internal, $"Failed to list files in '{directory}'", ex.Message)); }
    }

    /// <summary>Returns the size of a file in bytes.</summary>
    public static Result<long> GetFileSizeBytes(string path)
    {
        try
        {
            if (!File.Exists(path)) return Error.NotFound(ErrorCode.FileNotFound, $"File not found: {path}");
            return new FileInfo(path).Length;
        }
        catch (Exception ex) { return Error.Internal(ErrorCode.Internal, $"Failed to get file size for '{path}'", ex.Message); }
    }

    /// <summary>Returns true if the file exists.</summary>
    public static bool FileExists(string path) => File.Exists(path);

    /// <summary>Returns true if the directory exists.</summary>
    public static bool DirectoryExists(string path) => Directory.Exists(path);
}
