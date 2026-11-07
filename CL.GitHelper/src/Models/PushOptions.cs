
namespace CL.GitHelper.Models;

/// <summary>
/// Options controlling a <c>git push</c> operation.
/// </summary>
public sealed class PushOptions
{
    /// <summary>Remote to push to (default: "origin").</summary>
    public string RemoteName { get; set; } = "origin";

    /// <summary>Branch to push. Null pushes the currently checked-out branch.</summary>
    public string? BranchName { get; set; }

    /// <summary>Whether to force-push (rewrites remote history — use with care).</summary>
    public bool Force { get; set; }

    /// <summary>
    /// Also push all local tags. Tags are never force-pushed, even with <see cref="Force"/>, so a
    /// tag that already points elsewhere on the remote is rejected (<c>git.push_rejected</c>).
    /// </summary>
    public bool PushTags { get; set; }

    /// <summary>
    /// Invoked with progress updates during the push. May be null. It runs on the libgit2 worker thread,
    /// inside the native call: keep it short and never block in it (a callback that waits holds the
    /// transfer and the repository's lock), and hand UI or logging work off to another thread. Exceptions
    /// it throws are logged and ignored.
    /// </summary>
    public Action<GitProgress>? OnProgress { get; set; }

    /// <summary>Token that can cancel the operation, also mid-transfer.</summary>
    public CancellationToken CancellationToken { get; set; }
}
