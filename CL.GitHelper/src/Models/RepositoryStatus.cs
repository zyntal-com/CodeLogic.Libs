
namespace CL.GitHelper.Models;

/// <summary>
/// Working-tree status of a repository.
/// </summary>
public sealed class RepositoryStatus
{
    /// <summary>Whether the repository has any uncommitted changes.</summary>
    public bool IsDirty { get; set; }

    /// <summary>Files modified in the working tree but not staged.</summary>
    public List<FileStatusEntry> ModifiedFiles { get; set; } = [];

    /// <summary>Files staged for the next commit.</summary>
    public List<FileStatusEntry> StagedFiles { get; set; } = [];

    /// <summary>Files not tracked by Git.</summary>
    public List<FileStatusEntry> UntrackedFiles { get; set; } = [];

    /// <summary>Files ignored via .gitignore.</summary>
    public List<FileStatusEntry> IgnoredFiles { get; set; } = [];

    /// <summary>Files in a merge-conflict state.</summary>
    public List<FileStatusEntry> ConflictedFiles { get; set; } = [];

    /// <summary>Total count of changed (modified + staged + untracked) files.</summary>
    public int TotalChangedFiles => ModifiedFiles.Count + StagedFiles.Count + UntrackedFiles.Count;
}
