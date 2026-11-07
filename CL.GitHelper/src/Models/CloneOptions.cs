
namespace CL.GitHelper.Models;

// ── Operation options ─────────────────────────────────────────────────────────

/// <summary>
/// Options controlling a <c>git clone</c> operation.
/// </summary>
public sealed class CloneOptions
{
    /// <summary>Branch to check out after cloning. Null uses the remote's default branch.</summary>
    public string? BranchName { get; set; }

    /// <summary>Recursively initialise and update submodules after cloning.</summary>
    public bool RecurseSubmodules { get; set; }

    /// <summary>Create a bare repository (no working tree).</summary>
    public bool Bare { get; set; }

    /// <summary>
    /// Invoked with progress updates during the clone. May be null. It runs on the libgit2 worker thread,
    /// inside the native call: keep it short and never block in it (a callback that waits holds the
    /// transfer and the repository's lock), and hand UI or logging work off to another thread. Exceptions
    /// it throws are logged and ignored.
    /// </summary>
    public Action<GitProgress>? OnProgress { get; set; }

    /// <summary>Token that can cancel a long-running clone, also mid-transfer.</summary>
    public CancellationToken CancellationToken { get; set; }
}
