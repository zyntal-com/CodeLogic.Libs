
namespace CL.GitHelper.Models;

/// <summary>
/// Options controlling a <c>git commit</c> operation.
/// </summary>
public sealed class CommitOptions
{
    /// <summary>Commit message. Must not be empty.</summary>
    public string Message { get; set; } = "";

    /// <summary>Override the author name. Null reads from the repository's <c>user.name</c> config.</summary>
    public string? AuthorName { get; set; }

    /// <summary>Override the author e-mail. Null reads from the repository's <c>user.email</c> config.</summary>
    public string? AuthorEmail { get; set; }

    /// <summary>
    /// Specific files to stage before committing.
    /// Null commits whatever is already staged in the index.
    /// </summary>
    public List<string>? FilesToStage { get; set; }

    /// <summary>Cancellation token for the commit operation.</summary>
    public CancellationToken CancellationToken { get; set; }
}
