
namespace CL.GitHelper.Models;

/// <summary>
/// Options controlling a <c>git fetch</c> operation.
/// </summary>
public sealed class FetchOptions
{
    /// <summary>Name of the remote to fetch from (default: "origin").</summary>
    public string RemoteName { get; set; } = "origin";

    /// <summary>Delete remote-tracking references that no longer exist on the remote.</summary>
    public bool Prune { get; set; }

    /// <summary>Also fetch tags from the remote.</summary>
    public bool FetchTags { get; set; } = true;

    /// <summary>
    /// Invoked with progress updates during the fetch. May be null. It runs on the libgit2 worker thread,
    /// inside the native call: keep it short and never block in it (a callback that waits holds the
    /// transfer and the repository's lock), and hand UI or logging work off to another thread. Exceptions
    /// it throws are logged and ignored.
    /// </summary>
    public Action<GitProgress>? OnProgress { get; set; }

    /// <summary>Token that can cancel the operation, also mid-transfer.</summary>
    public CancellationToken CancellationToken { get; set; }
}
