
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
