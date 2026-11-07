
namespace CL.GitHelper.Models;

/// <summary>
/// Options controlling a <c>git pull</c> operation.
/// </summary>
public sealed class PullOptions
{
    /// <summary>
    /// Name of the remote to pull from (default: "origin"). When left at "origin",
    /// <see cref="FetchOptions"/>' <see cref="CL.GitHelper.Models.FetchOptions.RemoteName"/> is used instead.
    /// The current branch is integrated with its upstream on that remote, or with the
    /// remote branch of the same name when it has no upstream there.
    /// </summary>
    public string RemoteName { get; set; } = "origin";

    /// <summary>Integration strategy to apply after fetch (merge, rebase or fast-forward only).</summary>
    public MergeStrategy Strategy { get; set; } = MergeStrategy.Merge;

    /// <summary>
    /// Fetch options used for the fetch step (prune, tags, progress). Its cancellation token, when
    /// set, applies to the fetch; otherwise <see cref="CancellationToken"/> does.
    /// </summary>
    public FetchOptions FetchOptions { get; set; } = new();

    /// <summary>Token that can cancel the operation.</summary>
    public CancellationToken CancellationToken { get; set; }
}
