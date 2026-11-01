using System.Text;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Registry;

/// <summary>
/// The hold one transfer has on a resumable part file. In this process it is an entry in a shared table; across
/// processes it is a lock marker created create-only beside the part file (<c>&lt;part&gt;.lock</c>) that identifies
/// its owner, read back to confirm it. The marker holds a random owner id and salted hashes of the machine name,
/// process id and start time, never the values themselves: a part file on a shared server does not disclose host
/// names or process ids, and only the owner's machine can recognise its own markers. A marker whose owner is gone is taken over: on the same machine when its
/// process no longer runs, from another machine only once it is older than <see cref="StaleAfter"/>. The hold is
/// kept until the part file has been promoted, so nobody appends to it between the last write and the commit.
/// </summary>
internal sealed class PartLease
{
    /// <summary>How old a marker from another machine must be before its owner is presumed gone.</summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);

    /// <summary>How long a marker written where create-only is not atomic settles before it is read back.</summary>
    internal static readonly TimeSpan ReadBackDelay = TimeSpan.FromSeconds(1);

    private const string MarkerHeader = "cl-storage-part-lock/2";

    /// <summary>The 5.0 marker format, which named the machine and process in clear text; still read.</summary>
    private const string LegacyMarkerHeader = "cl-storage-part-lock/1";

    /// <summary>Part files being written in this process, so two writers never append into one.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> ActiveParts = new(StringComparer.Ordinal);

    private static readonly (int ProcessId, long StartedTicks) ProcessIdentity = BuildProcessIdentity();

    private readonly IStorageService _destination;
    private readonly string _partKey;
    private readonly string _content;
    private int _released;

    private PartLease(IStorageService destination, string staging, string partKey, string content)
    {
        _destination = destination;
        Staging = staging;
        _partKey = partKey;
        _content = content;
    }

    /// <summary>The part file held.</summary>
    public string Staging { get; }

    /// <summary>The lock marker beside a part file.</summary>
    internal static string MarkerPath(string staging) => staging + ".lock";

    private static string PartKey(IStorageService destination, string staging) =>
        $"{destination.Provider}\n{destination.Root}\n{staging}";

    /// <summary>
    /// Takes the part file for this transfer, or returns null when another transfer holds it. Fails only when the
    /// marker could not be written or read for a reason other than being taken.
    /// </summary>
    internal static async Task<Result<PartLease?>> AcquireAsync(IStorageService destination, string staging, bool createParents, CancellationToken cancellationToken)
    {
        var partKey = PartKey(destination, staging);
        if (!ActiveParts.TryAdd(partKey, 0))
            return Result<PartLease?>.Success(null);
        var content = FormatMarker(Guid.NewGuid().ToString("N"), Environment.MachineName, ProcessIdentity.ProcessId, ProcessIdentity.StartedTicks);
        var lease = new PartLease(destination, staging, partKey, content);
        var handedOut = false;
        try
        {
            var marker = MarkerPath(staging);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var created = await destination.UploadAsync(
                    marker,
                    new MemoryStream(Encoding.UTF8.GetBytes(content)),
                    new StorageUploadOptions { Overwrite = false, CreateParents = createParents, ContentType = "text/plain" },
                    cancellationToken).ConfigureAwait(false);
                if (created.IsSuccess)
                {
                    // Read back: where create-only is only checked before the write (FTP, SFTP), a racing writer
                    // that checked before this write lands its marker just after it, and the last marker wins. The
                    // read waits a moment so that marker is there to see. The re-check before the promote is not
                    // enough on its own: by then both writers may have appended to the part file.
                    if (!destination.Capabilities.Supports(StorageFeature.ConditionalCreate))
                        await Task.Delay(ReadBackDelay, cancellationToken).ConfigureAwait(false);
                    var owned = await lease.StillOwnedAsync(cancellationToken).ConfigureAwait(false);
                    if (owned.IsFailure)
                        return Result<PartLease?>.Failure(owned.Error!);
                    if (!owned.Value)
                        return Result<PartLease?>.Success(null);
                    handedOut = true;
                    return Result<PartLease?>.Success(lease);
                }
                // A connection that cannot write a marker create-only cannot share a part file safely: the write
                // uses a private staging object instead (not resumable).
                if (created.Error!.Code == StorageErrors.UnsupportedCode)
                    return Result<PartLease?>.Success(null);
                if (created.Error.Code != StorageErrors.ConflictCode)
                    return Result<PartLease?>.Failure(created.Error);
                if (attempt > 0)
                    return Result<PartLease?>.Success(null);
                // Another marker is there: it is taken over only when its owner is provably gone.
                var existing = await ReadMarkerAsync(destination, marker, cancellationToken).ConfigureAwait(false);
                if (existing.IsFailure)
                    return existing.Error!.Code == StorageErrors.NotFoundCode ? Result<PartLease?>.Success(null) : Result<PartLease?>.Failure(existing.Error!);
                if (!await IsStaleAsync(destination, marker, existing.Value!, cancellationToken).ConfigureAwait(false))
                    return Result<PartLease?>.Success(null);
                // Removed only while it is still the marker judged stale, so two takers do not remove each other's.
                var again = await ReadMarkerAsync(destination, marker, cancellationToken).ConfigureAwait(false);
                if (again.IsFailure || again.Value != existing.Value)
                    return Result<PartLease?>.Success(null);
                var removed = await destination.DeleteAsync(marker, new StorageDeleteOptions { IgnoreMissing = true }, cancellationToken).ConfigureAwait(false);
                if (removed.IsFailure)
                    return Result<PartLease?>.Failure(removed.Error!);
            }
            return Result<PartLease?>.Success(null);
        }
        finally
        {
            if (!handedOut)
                ActiveParts.TryRemove(partKey, out _);
        }
    }

    /// <summary>Whether a part file is held: by this process, or by a marker (one that cannot be read counts as held).</summary>
    internal static async Task<bool> InUseAsync(IStorageService destination, string staging)
    {
        if (ActiveParts.ContainsKey(PartKey(destination, staging)))
            return true;
        try
        {
            var marker = await destination.ExistsAsync(MarkerPath(staging), CancellationToken.None).ConfigureAwait(false);
            return marker.IsFailure || marker.Value;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>Whether the marker still names this transfer; another transfer may have taken a stale one over.</summary>
    internal async Task<Result<bool>> StillOwnedAsync(CancellationToken cancellationToken)
    {
        var current = await ReadMarkerAsync(_destination, MarkerPath(Staging), cancellationToken).ConfigureAwait(false);
        if (current.IsFailure)
            return current.Error!.Code == StorageErrors.NotFoundCode ? Result<bool>.Success(false) : Result<bool>.Failure(current.Error!);
        return Result<bool>.Success(current.Value == _content);
    }

    /// <summary>Gives the part file up: the marker is removed while it is still this transfer's. Never throws.</summary>
    internal async Task ReleaseAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
            return;
        try
        {
            var owned = await StillOwnedAsync(CancellationToken.None).ConfigureAwait(false);
            if (owned.IsSuccess && owned.Value)
                await _destination.DeleteAsync(MarkerPath(Staging), new StorageDeleteOptions { IgnoreMissing = true }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A marker left behind names this process: the next attempt here takes it over.
        }
        finally
        {
            ActiveParts.TryRemove(_partKey, out _);
        }
    }

    private static async Task<Result<string>> ReadMarkerAsync(IStorageService destination, string marker, CancellationToken cancellationToken)
    {
        Result<Stream> download;
        try { download = await destination.DownloadAsync(marker, new StorageDownloadOptions(), cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException) { return Result<string>.Failure(StorageErrors.FromException(error, "Read part file lock")); }
        if (download.IsFailure)
            return Result<string>.Failure(download.Error!);
        try
        {
            await using var stream = download.Value!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var buffer = new char[4096];
            var read = await reader.ReadBlockAsync(buffer, cancellationToken).ConfigureAwait(false);
            return Result<string>.Success(new string(buffer, 0, read));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return Result<string>.Failure(StorageErrors.FromException(error, "Read part file lock"));
        }
    }

    /// <summary>
    /// The marker text: the header, a random owner id, then salted hashes (keyed by the owner id) of the machine name,
    /// of machine and process id, and of machine, process id and start time.
    /// </summary>
    internal static string FormatMarker(string ownerId, string machineName, int processId, long startedTicks) =>
        string.Join('\n',
            MarkerHeader,
            ownerId,
            Tag(ownerId, machineName),
            Tag(ownerId, machineName, Invariant(processId)),
            Tag(ownerId, machineName, Invariant(processId), Invariant(startedTicks)));

    private static string Invariant(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Tag(string ownerId, params ReadOnlySpan<string> parts)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', [ownerId, .. parts])));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }

    /// <summary>
    /// Whether the owner of a marker is gone: a process on this machine that no longer runs (or this process,
    /// which holds no such part file now), or a marker from elsewhere older than <see cref="StaleAfter"/>.
    /// </summary>
    private static async Task<bool> IsStaleAsync(IStorageService destination, string marker, string content, CancellationToken cancellationToken)
    {
        var lines = content.Split('\n');
        var machine = Environment.MachineName;
        if (lines.Length == 5 && lines[0] == MarkerHeader && lines[2] == Tag(lines[1], machine))
        {
            var (ownerId, pidTag, processTag) = (lines[1], lines[3], lines[4]);
            // This process holds no such part file (the in-process table said so), so its own marker is a leftover.
            var ownPid = Invariant(ProcessIdentity.ProcessId);
            if (pidTag == Tag(ownerId, machine, ownPid) && processTag == Tag(ownerId, machine, ownPid, Invariant(ProcessIdentity.StartedTicks)))
                return true;
            return !IsRunning(ownerId, pidTag, processTag);
        }
        if (lines.Length == 5 && lines[0] == LegacyMarkerHeader && lines[2] == machine &&
            int.TryParse(lines[3], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid) &&
            long.TryParse(lines[4], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var started))
        {
            if (pid == ProcessIdentity.ProcessId && started == ProcessIdentity.StartedTicks)
                return true;
            return !IsRunning(pid, started);
        }
        var info = await destination.GetInfoAsync(marker, cancellationToken).ConfigureAwait(false);
        return info.IsSuccess && info.Value!.LastModified is { } modified && DateTimeOffset.UtcNow - modified > StaleAfter;
    }

    /// <summary>
    /// Whether the process a hashed marker names still runs on this machine: running processes are matched against
    /// the process-id hash, then against the start-time hash. An owner that could not read its own start time wrote
    /// zero, and a matching process whose start time cannot be read is presumed to be the owner.
    /// </summary>
    private static bool IsRunning(string ownerId, string pidTag, string processTag)
    {
        var machine = Environment.MachineName;
        System.Diagnostics.Process[] processes;
        try { processes = System.Diagnostics.Process.GetProcesses(); }
        catch (Exception) { return true; } // cannot tell: presumed running
        var running = false;
        foreach (var process in processes)
        {
            using (process)
            {
                if (running) continue;
                var id = Invariant(process.Id);
                if (Tag(ownerId, machine, id) != pidTag) continue;
                if (processTag == Tag(ownerId, machine, id, Invariant(0)))
                {
                    running = true;
                    continue;
                }
                try { running = processTag == Tag(ownerId, machine, id, Invariant(process.StartTime.ToUniversalTime().Ticks)); }
                catch (Exception) { running = true; } // cannot tell: presumed running
            }
        }
        return running;
    }

    private static bool IsRunning(int pid, long startedTicks)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return startedTicks == 0 || process.StartTime.ToUniversalTime().Ticks == startedTicks;
        }
        catch (ArgumentException)
        {
            return false; // no process with that id
        }
        catch (Exception)
        {
            return true; // cannot tell: presumed running
        }
    }

    private static (int ProcessId, long StartedTicks) BuildProcessIdentity()
    {
        long started = 0;
        try
        {
            using var current = System.Diagnostics.Process.GetCurrentProcess();
            started = current.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception)
        {
            // Without a start time the process id alone identifies the owner.
        }
        return (Environment.ProcessId, started);
    }
}
