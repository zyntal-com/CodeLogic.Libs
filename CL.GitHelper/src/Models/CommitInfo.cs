
namespace CL.GitHelper.Models;

/// <summary>
/// Information about a single Git commit.
/// </summary>
public sealed class CommitInfo
{
    /// <summary>Full 40-character SHA.</summary>
    public string Sha { get; set; } = "";

    /// <summary>Abbreviated 7-character SHA.</summary>
    public string ShortSha => Sha.Length > 7 ? Sha[..7] : Sha;

    /// <summary>Full commit message.</summary>
    public string Message { get; set; } = "";

    /// <summary>First line of the commit message.</summary>
    public string ShortMessage => Message.Split('\n')[0];

    /// <summary>Author name.</summary>
    public string AuthorName { get; set; } = "";

    /// <summary>Author e-mail address.</summary>
    public string AuthorEmail { get; set; } = "";

    /// <summary>When the author wrote the commit.</summary>
    public DateTime AuthorDate { get; set; }

    /// <summary>Committer name (may differ from author in rebases/cherry-picks).</summary>
    public string CommitterName { get; set; } = "";

    /// <summary>Committer e-mail address.</summary>
    public string CommitterEmail { get; set; } = "";

    /// <summary>When the commit was recorded in the repository.</summary>
    public DateTime CommitDate { get; set; }

    /// <summary>SHAs of parent commits (empty for the initial commit, two for merges).</summary>
    public List<string> ParentShas { get; set; } = [];
}
