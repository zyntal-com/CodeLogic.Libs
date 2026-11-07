
namespace CL.GitHelper.Models;

// ── Repository data ───────────────────────────────────────────────────────────

/// <summary>
/// Summary information about a Git repository.
/// </summary>
public sealed class RepositoryInfo
{
    /// <summary>Configuration ID that created this repository.</summary>
    public string Id { get; set; } = "";

    /// <summary>Human-readable name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Absolute path to the local working directory.</summary>
    public string LocalPath { get; set; } = "";

    /// <summary>URL of the first configured remote ("origin").</summary>
    public string RemoteUrl { get; set; } = "";

    /// <summary>Currently checked-out branch name.</summary>
    public string CurrentBranch { get; set; } = "";

    /// <summary>Full SHA of the HEAD commit.</summary>
    public string HeadCommitSha { get; set; } = "";

    /// <summary>Whether this is a bare repository.</summary>
    public bool IsBare { get; set; }

    /// <summary>Repository operation state (Normal, Merging, Rebasing, etc.).</summary>
    public string State { get; set; } = "Normal";

    /// <summary>Number of local branches.</summary>
    public int LocalBranchCount { get; set; }

    /// <summary>Number of remote-tracking branches.</summary>
    public int RemoteBranchCount { get; set; }

    /// <summary>Number of tags.</summary>
    public int TagCount { get; set; }

    /// <summary>Number of stashes.</summary>
    public int StashCount { get; set; }

    /// <summary>Whether the working tree has uncommitted changes.</summary>
    public bool IsDirty { get; set; }

    /// <summary>Count of modified (unstaged) files.</summary>
    public int ModifiedFiles { get; set; }

    /// <summary>Count of staged (index) files.</summary>
    public int StagedFiles { get; set; }

    /// <summary>Count of untracked files.</summary>
    public int UntrackedFiles { get; set; }
}
