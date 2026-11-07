
namespace CL.GitHelper.Models;

// ── Enumerations ──────────────────────────────────────────────────────────────

/// <summary>
/// Integration strategy for <c>git pull</c>.
/// </summary>
public enum MergeStrategy
{
    /// <summary>Standard three-way merge commit.</summary>
    Merge,

    /// <summary>Rebase local commits on top of the fetched tip.</summary>
    Rebase,

    /// <summary>Only accept fast-forward merges — abort if not possible.</summary>
    FastForwardOnly
}
