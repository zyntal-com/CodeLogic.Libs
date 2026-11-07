
namespace CL.GitHelper.Models;

/// <summary>
/// Status of a single file in the working tree or index.
/// </summary>
public sealed class FileStatusEntry
{
    /// <summary>Repository-relative file path.</summary>
    public string FilePath { get; set; } = "";

    /// <summary>Status flags as a string (e.g., "ModifiedInWorkdir").</summary>
    public string Status { get; set; } = "";

    /// <summary>Whether the file is staged in the index.</summary>
    public bool IsStaged { get; set; }

    /// <summary>Previous path, populated only for renamed files.</summary>
    public string? OldFilePath { get; set; }
}
