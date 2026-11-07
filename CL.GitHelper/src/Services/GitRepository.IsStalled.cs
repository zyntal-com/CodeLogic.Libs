using CL.GitHelper.Errors;
using CL.GitHelper.Models;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;

// Alias our model types that clash with LibGit2Sharp names.
using GitCloneOptions = CL.GitHelper.Models.CloneOptions;
using GitFetchOptions = CL.GitHelper.Models.FetchOptions;
using GitPullOptions = CL.GitHelper.Models.PullOptions;
using GitPushOptions = CL.GitHelper.Models.PushOptions;
using GitCommitOptions = CL.GitHelper.Models.CommitOptions;
using GitRepoStatus = CL.GitHelper.Models.RepositoryStatus;

namespace CL.GitHelper.Services;

public sealed partial class GitRepository
{

    // ── Locking ───────────────────────────────────────────────────────────────

    /// <summary>
    /// True while a clone, fetch or push on this repository has run past its timeout plus the stall
    /// grace without libgit2 returning (a connection that hangs before any progress callback). Every
    /// operation returns <see cref="GitErrors.TimeoutCode"/> at once until the transfer returns.
    /// </summary>
    public bool IsStalled
    {
        get { lock (_stallLock) return _stalled; }
    }

    /// <summary>
    /// Takes the operation lock, or returns the error to hand back instead: the repository is disposed,
    /// or a transfer on it has stalled (a caller already waiting when the stall is declared gets it too).
    /// </summary>
    private async Task<Error?> EnterAsync(CancellationToken cancellationToken)
    {
        if (_disposed) return DisposedError();

        CancellationToken stall;
        lock (_stallLock)
        {
            if (_stalled) return StalledError();
            stall = _stallSignal.Token;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stall);
        try
        {
            await _lock.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stall.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return StalledError();
        }

        if (_disposed)
        {
            _lock.Release();
            return DisposedError();
        }
        return null;
    }

    /// <summary>Releases the operation lock; finishes a deferred <see cref="Dispose"/> first.</summary>
    private void Leave(Transfer? transfer = null)
    {
        transfer?.Complete();   // stops the watchdog before the stall flag is cleared
        ClearStalled();
        if (_disposed) ReleaseHandle();
        _lock.Release();
    }

    private void MarkStalled(string operation)
    {
        CancellationTokenSource signal;
        lock (_stallLock)
        {
            if (_stalled) return;
            _stalled = true;
            signal = _stallSignal;
        }
        _logger?.Error($"[GitHelper] {operation} of '{_config.Id}': transfer stalled — libgit2 has not returned " +
            $"{_stallGrace.TotalSeconds:F0}s after the {_timeoutSeconds}s timeout. Operations on this repository " +
            $"return {GitErrors.TimeoutCode} until it does.");
        signal.Cancel();   // outside the lock: continuations of released waiters may run inline
    }

    private void ClearStalled()
    {
        lock (_stallLock)
        {
            if (!_stalled) return;
            _stalled = false;
            // The old source is not disposed: a waiter may still be linking to its token. It has no timer, so it is just collected.
            _stallSignal = new CancellationTokenSource();
        }
        _logger?.Info($"[GitHelper] The stalled transfer of '{_config.Id}' returned; operations resume.");
    }

    private Error StalledError() => Error.Timeout(GitErrors.TimeoutCode,
        $"A transfer on repository '{_config.Id}' has stalled (no response from libgit2 since its timeout); " +
        "the operation was not started.");

    private Error DisposedError() => Error.Unavailable(GitErrors.RepositoryDisposedCode,
        $"Repository '{_config.Id}' instance was disposed; call GetRepositoryAsync again.");

    /// <summary>Runs a read of the native repository under the lock on a worker thread.</summary>
    private async Task<Result<T>> ReadAsync<T>(string operation, string failureCode, Func<Repository, T> read)
    {
        try
        {
            if (await EnterAsync(CancellationToken.None).ConfigureAwait(false) is { } busy)
                return busy;
            try
            {
                return await Task.Run(() => read(EnsureOpen())).ConfigureAwait(false);
            }
            finally
            {
                Leave();
            }
        }
        catch (Exception ex)
        {
            return Fail(operation, ex, failureCode, quiet: true);
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Builds a LibGit2Sharp credentials callback for HTTPS authentication from the
    /// repository configuration. Returns null when no credentials are configured (anonymous).
    /// </summary>
    /// <remarks>
    /// When only <see cref="RepositoryConfiguration.Password"/> is set, the username is sent
    /// as <c>x-access-token</c> — the canonical placeholder GitHub accepts for PAT-only auth.
    /// When both are set, both are forwarded verbatim. SSH key fields on the configuration
    /// are preserved for future use but are not currently wired (the LibGit2Sharp 0.30 managed
    /// binary ships without the native SSH transport).
    /// </remarks>
    private CredentialsHandler? BuildCredentialsHandler() => CreateCredentialsHandler(_config, _logger);

    /// <summary>
    /// Credentials callback that hands out the configured user name and token only to the
    /// configured repository's scheme, host and port (<see cref="GitSecurity.CredentialsAllowedFor"/>).
    /// Any other host — a submodule elsewhere, a redirect, a second remote — gets empty credentials,
    /// so the request fails with <see cref="GitErrors.AuthenticationFailedCode"/> instead of leaking the token.
    /// Returns null when no token is configured (anonymous access).
    /// </summary>
    internal static CredentialsHandler? CreateCredentialsHandler(RepositoryConfiguration config, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(config.Password)) return null;

        var configuredUrl = config.RepositoryUrl;
        var username = string.IsNullOrWhiteSpace(config.Username) ? "x-access-token" : config.Username!;
        var password = config.Password;

        // Never throw here: the callback runs inside a native libgit2 call.
        return (url, _, _) =>
        {
            if (GitSecurity.CredentialsAllowedFor(configuredUrl, url))
                return new UsernamePasswordCredentials { Username = username, Password = password };

            logger?.Warning(
                $"[GitHelper] Repository '{config.Id}': {GitSecurity.Redact(url, password)} asked for credentials, " +
                "but they are configured for another host; none were sent.");
            return new UsernamePasswordCredentials { Username = "", Password = "" };
        };
    }

    /// <summary>
    /// True when <paramref name="path"/> holds a git repository: a working tree with <c>.git</c>
    /// (directory or worktree file) or a bare repository.
    /// </summary>
    internal static bool IsCloned(string path) => Directory.Exists(path) && Repository.IsValid(path);

    /// <summary>Wires credentials, progress reporting and cancellation into a fetch or clone.</summary>
    private void ApplyFetchCallbacks(FetchOptionsBase target, Transfer transfer,
        Action<GitProgress>? onProgress, GitDiagnostics diag)
    {
        target.CredentialsProvider = BuildCredentialsHandler();
        target.OnTransferProgress = progress =>
        {
            diag.ObjectCount = progress.ReceivedObjects;
            diag.BytesTransferred = progress.ReceivedBytes;
            var receiving = progress.ReceivedObjects < progress.TotalObjects;
            Report(onProgress, new GitProgress
            {
                Stage = receiving ? "Receiving objects" : "Indexing objects",
                ProgressPercentage = Percent(receiving ? progress.ReceivedObjects : progress.IndexedObjects, progress.TotalObjects),
                ObjectsReceived = progress.ReceivedObjects,
                TotalObjects = progress.TotalObjects,
                BytesReceived = progress.ReceivedBytes
            });
            // Returning false makes libgit2 abort the transfer (UserCancelledException).
            return transfer.Continue;
        };
        target.OnProgress = serverText =>
        {
            Report(onProgress, new GitProgress { Stage = "Remote", Message = serverText.TrimEnd() });
            return transfer.Continue;
        };
    }

    private void Report(Action<GitProgress>? onProgress, GitProgress progress)
    {
        if (onProgress is null) return;
        // The callback runs inside a native libgit2 call: an exception must not escape.
        try { onProgress(progress); }
        catch (Exception ex) { _logger?.Debug($"[GitHelper] OnProgress callback of '{_config.Id}' threw: {Redact(ex.Message)}"); }
    }

    private static int Percent(long done, long total) =>
        total <= 0 ? 0 : (int)Math.Clamp(done * 100 / total, 0, 100);

    private string Redact(string? text) => GitSecurity.Redact(text, _config.Password);

    /// <summary>
    /// Logs a failed operation and maps the exception to an error. Credentials in URLs and the
    /// configured token are redacted from both. A transfer stopped by the timeout becomes
    /// <see cref="GitErrors.TimeoutCode"/>. Read failures (<paramref name="quiet"/>) are logged at
    /// debug level only, with the full exception.
    /// </summary>
    private Error Fail(string operation, Exception ex, string fallbackCode, Transfer? transfer = null,
        string notFoundCode = GitErrors.NotFoundCode, bool quiet = false)
    {
        var message = Redact(ex.Message);
        if (transfer?.TimedOut == true)
        {
            _logger?.Error($"{operation} of '{_config.Id}' timed out after {transfer.TimeoutSeconds}s");
            return Error.Timeout(GitErrors.TimeoutCode,
                $"{operation} timed out after {transfer.TimeoutSeconds}s.", ex.GetType().Name);
        }

        if (quiet)
        {
            _logger?.Debug($"{operation} of '{_config.Id}' failed: {Redact(ex.ToString())}");
        }
        else
        {
            _logger?.Error($"{operation} of '{_config.Id}' failed: {message}");
            _logger?.Debug($"{operation} failure detail: {Redact(ex.ToString())}");
        }
        return GitErrors.FromException(ex, $"{operation} failed: {message}", fallbackCode, notFoundCode);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            // libgit2 writes read-only pack files; clear the flag so the delete succeeds on Windows.
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(path, recursive: true);
        }
        catch (Exception) { /* best effort */ }
    }
}
