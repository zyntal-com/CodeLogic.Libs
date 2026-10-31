using CodeLogic.Core.Results;
using LibGit2Sharp;

namespace CL.GitHelper.Errors;

/// <summary>
/// Creates the <see cref="Error"/> values returned by CL.GitHelper operations, with stable
/// <c>git.*</c> codes. Compare <see cref="Error.Code"/> against the constants here, or switch
/// on the error kind (<see cref="NotFoundError"/>, <see cref="ValidationError"/>, ...).
/// When an error comes from a caught exception, <see cref="Error.Details"/> holds the exception
/// type name; the exception itself is logged by the library.
/// </summary>
public static class GitErrors
{
    /// <summary>The local repository does not exist (not cloned yet) — <see cref="NotFoundError"/>.</summary>
    public const string RepositoryNotFoundCode = "git.repository_not_found";
    /// <summary>The named remote is not configured in the repository — <see cref="NotFoundError"/>.</summary>
    public const string RemoteNotFoundCode = "git.remote_not_found";
    /// <summary>The named branch, or the reset target, could not be resolved — <see cref="NotFoundError"/>.</summary>
    public const string BranchNotFoundCode = "git.branch_not_found";
    /// <summary>A reference, object or path named by the caller does not exist — <see cref="NotFoundError"/>.</summary>
    public const string NotFoundCode = "git.not_found";
    /// <summary>The commit message is empty — <see cref="ValidationError"/>.</summary>
    public const string EmptyCommitMessageCode = "git.empty_commit_message";
    /// <summary>The commit would record no changes — <see cref="ValidationError"/>.</summary>
    public const string NothingToCommitCode = "git.nothing_to_commit";
    /// <summary>The repository URL uses SSH, which is not supported — <see cref="ValidationError"/>.</summary>
    public const string SshNotSupportedCode = "git.ssh_not_supported";
    /// <summary>The remote rejected the credentials, or required ones that were not configured — <see cref="UnauthorizedError"/>.</summary>
    public const string AuthenticationFailedCode = "git.authentication_failed";
    /// <summary>The clone target directory exists and is not empty — <see cref="ConflictError"/>.</summary>
    public const string DirectoryNotEmptyCode = "git.directory_not_empty";
    /// <summary>The working tree has uncommitted changes that the operation would discard — <see cref="ConflictError"/>.</summary>
    public const string UncommittedChangesCode = "git.uncommitted_changes";
    /// <summary>A checkout would overwrite local changes — <see cref="ConflictError"/>.</summary>
    public const string CheckoutConflictCode = "git.checkout_conflict";
    /// <summary>The remote rejected a non-fast-forward push — <see cref="ConflictError"/>.</summary>
    public const string NonFastForwardCode = "git.non_fast_forward";
    /// <summary>The operation was cancelled by the caller — <see cref="UnavailableError"/>.</summary>
    public const string CancelledCode = "git.cancelled";

    /// <summary>Clone failed for another reason — <see cref="InternalError"/>.</summary>
    public const string CloneFailedCode = "git.clone_failed";
    /// <summary>Fetch failed for another reason — <see cref="InternalError"/>.</summary>
    public const string FetchFailedCode = "git.fetch_failed";
    /// <summary>Pull (merge step) failed for another reason — <see cref="InternalError"/>.</summary>
    public const string PullFailedCode = "git.pull_failed";
    /// <summary>Push failed for another reason — <see cref="InternalError"/>.</summary>
    public const string PushFailedCode = "git.push_failed";
    /// <summary>Commit failed for another reason — <see cref="InternalError"/>.</summary>
    public const string CommitFailedCode = "git.commit_failed";
    /// <summary>Reading the working-tree status failed — <see cref="InternalError"/>.</summary>
    public const string StatusFailedCode = "git.status_failed";
    /// <summary>Reading repository information failed — <see cref="InternalError"/>.</summary>
    public const string InfoFailedCode = "git.info_failed";
    /// <summary>Listing branches failed — <see cref="InternalError"/>.</summary>
    public const string ListBranchesFailedCode = "git.list_branches_failed";
    /// <summary>Checkout failed for another reason — <see cref="InternalError"/>.</summary>
    public const string CheckoutFailedCode = "git.checkout_failed";
    /// <summary>Hard reset failed for another reason — <see cref="InternalError"/>.</summary>
    public const string ResetFailedCode = "git.reset_failed";
    /// <summary>Reading the commit log failed — <see cref="InternalError"/>.</summary>
    public const string LogFailedCode = "git.log_failed";

    /// <summary>Creates a <see cref="RemoteNotFoundCode"/> error.</summary>
    /// <param name="remoteName">The remote that was not found.</param>
    public static Error RemoteNotFound(string remoteName) =>
        Error.NotFound(RemoteNotFoundCode, $"Remote '{remoteName}' not found");

    /// <summary>
    /// Maps an exception caught during a Git operation to an error of the matching kind.
    /// Known LibGit2Sharp exceptions get a specific code; everything else becomes an
    /// <see cref="InternalError"/> with <paramref name="fallbackCode"/>.
    /// </summary>
    /// <param name="ex">The caught exception.</param>
    /// <param name="message">Human-readable message (typically "&lt;Operation&gt; failed: &lt;ex.Message&gt;").</param>
    /// <param name="fallbackCode">Code used when the exception has no narrower mapping.</param>
    /// <param name="notFoundCode">Code used for a LibGit2Sharp <see cref="NotFoundException"/>.</param>
    public static Error FromException(Exception ex, string message, string fallbackCode, string notFoundCode = NotFoundCode)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var details = ex.GetType().Name;

        return ex switch
        {
            RepositoryNotFoundException => Error.NotFound(RepositoryNotFoundCode, message, details),
            NotFoundException => Error.NotFound(notFoundCode, message, details),
            EmptyCommitException => Error.Validation(NothingToCommitCode, message, details),
            CheckoutConflictException => Error.Conflict(CheckoutConflictCode, message, details),
            NonFastForwardException => Error.Conflict(NonFastForwardCode, message, details),
            OperationCanceledException or UserCancelledException => Error.Unavailable(CancelledCode, message, details),
            LibGit2SharpException when IsAuthenticationFailure(ex.Message) =>
                Error.Unauthorized(AuthenticationFailedCode, message, details),
            _ => Error.Internal(fallbackCode, message, details)
        };
    }

    // LibGit2Sharp has no dedicated exception type for auth failures; libgit2 reports them
    // through the message ("authentication required", "too many redirects or authentication
    // replays", "unexpected http status code: 401/403").
    private static bool IsAuthenticationFailure(string message) =>
        message.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("status code: 401", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("status code: 403", StringComparison.OrdinalIgnoreCase);
}
