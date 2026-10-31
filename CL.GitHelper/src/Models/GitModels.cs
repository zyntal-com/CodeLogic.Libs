namespace CL.GitHelper.Models;

// ── Diagnostics ───────────────────────────────────────────────────────────────

/// <summary>
/// Performance and transfer metrics captured during a Git operation. Returned as the success
/// value of <c>GitRepository.FetchAsync</c>, <c>PullAsync</c> and <c>PushAsync</c>.
/// </summary>
public sealed class GitDiagnostics
{
    /// <summary>UTC time when the operation started.</summary>
    public DateTime StartedAt { get; } = DateTime.UtcNow;

    /// <summary>UTC time when the operation finished. Null until <see cref="Complete"/> is called.</summary>
    public DateTime? CompletedAt { get; private set; }

    /// <summary>Total elapsed time. Available after <see cref="Complete"/>.</summary>
    public TimeSpan? Duration => CompletedAt.HasValue ? CompletedAt.Value - StartedAt : null;

    /// <summary>Number of Git objects received or sent.</summary>
    public int ObjectCount { get; set; }

    /// <summary>Total bytes transferred over the network.</summary>
    public long BytesTransferred { get; set; }

    /// <summary>Number of files changed by the operation.</summary>
    public int FilesChanged { get; set; }

    /// <summary>Number of commits created or processed.</summary>
    public int CommitsProcessed { get; set; }

    /// <summary>Timestamped diagnostic messages appended during the operation.</summary>
    public List<string> Messages { get; } = [];

    /// <summary>Arbitrary key/value metrics for extensibility.</summary>
    public Dictionary<string, object> Metrics { get; } = [];

    /// <summary>Records the completion time of the operation.</summary>
    public void Complete() => CompletedAt = DateTime.UtcNow;

    /// <summary>Appends a timestamped message to <see cref="Messages"/>.</summary>
    public void AddMessage(string message) =>
        Messages.Add($"[{DateTime.UtcNow:HH:mm:ss.fff}] {message}");

    /// <summary>Records an arbitrary metric.</summary>
    public void AddMetric(string name, object value) => Metrics[name] = value;
}

// ── Progress ──────────────────────────────────────────────────────────────────

/// <summary>
/// Progress snapshot reported during long-running Git operations such as clone or fetch.
/// </summary>
public sealed class GitProgress
{
    /// <summary>Current operation stage (e.g., "Counting objects", "Receiving objects").</summary>
    public string Stage { get; set; } = "";

    /// <summary>Progress percentage 0–100.</summary>
    public int ProgressPercentage { get; set; }

    /// <summary>Number of objects received so far.</summary>
    public int ObjectsReceived { get; set; }

    /// <summary>Total objects expected.</summary>
    public int TotalObjects { get; set; }

    /// <summary>Bytes received so far.</summary>
    public long BytesReceived { get; set; }

    /// <summary>Additional detail message.</summary>
    public string Message { get; set; } = "";

    /// <summary>UTC timestamp of this snapshot.</summary>
    public DateTime Timestamp { get; } = DateTime.UtcNow;
}

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

/// <summary>
/// Working-tree status of a repository.
/// </summary>
public sealed class RepositoryStatus
{
    /// <summary>Whether the repository has any uncommitted changes.</summary>
    public bool IsDirty { get; set; }

    /// <summary>Files modified in the working tree but not staged.</summary>
    public List<FileStatusEntry> ModifiedFiles { get; set; } = [];

    /// <summary>Files staged for the next commit.</summary>
    public List<FileStatusEntry> StagedFiles { get; set; } = [];

    /// <summary>Files not tracked by Git.</summary>
    public List<FileStatusEntry> UntrackedFiles { get; set; } = [];

    /// <summary>Files ignored via .gitignore.</summary>
    public List<FileStatusEntry> IgnoredFiles { get; set; } = [];

    /// <summary>Files in a merge-conflict state.</summary>
    public List<FileStatusEntry> ConflictedFiles { get; set; } = [];

    /// <summary>Total count of changed (modified + staged + untracked) files.</summary>
    public int TotalChangedFiles => ModifiedFiles.Count + StagedFiles.Count + UntrackedFiles.Count;
}

/// <summary>
/// Status of a single file in the working tree or index.
/// </summary>
public sealed class FileStatusEntry
{
    /// <summary>Repository-relative file path.</summary>
    public string FilePath { get; set; } = "";

    /// <summary>Status flags as a string (e.g., "ModifiedInWorkdir").</summary>
    public string Status { get; set; } = "";

    /// <summary>Whether the file is staged in the index.</summary>
    public bool IsStaged { get; set; }

    /// <summary>Previous path, populated only for renamed files.</summary>
    public string? OldFilePath { get; set; }
}

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

    /// <summary>Invoked with progress updates during the clone. May be null.</summary>
    public Action<GitProgress>? OnProgress { get; set; }

    /// <summary>Token that can cancel a long-running clone.</summary>
    public CancellationToken CancellationToken { get; set; }
}

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

    /// <summary>Invoked with progress updates during the fetch. May be null.</summary>
    public Action<GitProgress>? OnProgress { get; set; }

    /// <summary>Token that can cancel the operation.</summary>
    public CancellationToken CancellationToken { get; set; }
}

/// <summary>
/// Options controlling a <c>git pull</c> operation.
/// </summary>
public sealed class PullOptions
{
    /// <summary>Name of the remote to pull from (default: "origin").</summary>
    public string RemoteName { get; set; } = "origin";

    /// <summary>Integration strategy to apply after fetch.</summary>
    public MergeStrategy Strategy { get; set; } = MergeStrategy.Merge;

    /// <summary>Fetch options used for the fetch step.</summary>
    public FetchOptions FetchOptions { get; set; } = new();

    /// <summary>Token that can cancel the operation.</summary>
    public CancellationToken CancellationToken { get; set; }
}

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

    /// <summary>Also push all local tags.</summary>
    public bool PushTags { get; set; }

    /// <summary>Invoked with progress updates during the push. May be null.</summary>
    public Action<GitProgress>? OnProgress { get; set; }

    /// <summary>Token that can cancel the operation.</summary>
    public CancellationToken CancellationToken { get; set; }
}

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
