
namespace CL.GitHelper.Models;

/// <summary>
/// Information about a Git branch.
/// </summary>
public sealed class BranchInfo
{
    /// <summary>Canonical ref name (e.g., "refs/heads/main").</summary>
    public string Name { get; set; } = "";

    /// <summary>Friendly name shown in git output (e.g., "main").</summary>
    public string FriendlyName { get; set; } = "";

    /// <summary>Whether this is the currently checked-out branch.</summary>
    public bool IsCurrent { get; set; }

    /// <summary>Whether this is a remote-tracking branch.</summary>
    public bool IsRemote { get; set; }

    /// <summary>Whether this branch tracks a remote branch.</summary>
    public bool IsTracking { get; set; }

    /// <summary>Name of the tracked upstream branch, if any.</summary>
    public string? TrackedBranchName { get; set; }

    /// <summary>SHA at the tip of this branch.</summary>
    public string TipSha { get; set; } = "";

    /// <summary>Commits ahead of the upstream tracking branch.</summary>
    public int? AheadBy { get; set; }

    /// <summary>Commits behind the upstream tracking branch.</summary>
    public int? BehindBy { get; set; }
}
