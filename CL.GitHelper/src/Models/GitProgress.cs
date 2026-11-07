
namespace CL.GitHelper.Models;

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
