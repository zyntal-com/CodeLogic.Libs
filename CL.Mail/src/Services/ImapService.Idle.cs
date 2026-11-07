using CL.Mail.Models;
using CodeLogic.Core.Results;
using MailKit;
using MailKit.Net.Imap;

namespace CL.Mail.Services;

/// <summary>
/// Event arguments raised when new mail arrives during IMAP IDLE monitoring.
/// </summary>
public sealed class NewMailEventArgs : EventArgs
{
    /// <summary>Full IMAP path of the folder where new mail arrived.</summary>
    public required string FolderName { get; init; }

    /// <summary>Total message count in the folder after the new messages.</summary>
    public int MessageCount { get; init; }
}

// RFC 2177 IDLE: the background loop, its start/stop and the NewMailReceived event.
public sealed partial class ImapService
{
    private readonly Lock _idleLock = new();
    private CancellationTokenSource? _idleCts;    // stop requested
    private CancellationTokenSource? _idleAbort;  // hard abort when a graceful stop does not finish in time
    private Task? _idleTask;

    /// <summary>Fired when new mail is detected during IDLE monitoring.</summary>
    public event EventHandler<NewMailEventArgs>? NewMailReceived;

    /// <summary>
    /// Starts RFC 2177 IDLE monitoring on the given folder.
    /// Fires <see cref="NewMailReceived"/> when new messages arrive.
    /// <para>
    /// Requires <see cref="ImapConfig.EnableIdle"/>; when it is <c>false</c> the call fails with
    /// <see cref="MailErrorCodes.ImapConfigInvalid"/>. The loop reconnects after a dropped connection and
    /// re-opens the folder on the new connection. Other calls on this service interrupt IDLE while they run.
    /// </para>
    /// </summary>
    /// <param name="folder">IMAP folder to monitor (typically "INBOX").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<Result> StartIdleAsync(string folder, CancellationToken cancellationToken = default)
    {
        if (!_config.EnableIdle)
            return MailErrors.Validation(MailErrorCodes.ImapConfigInvalid, "IDLE is disabled: set Imap.EnableIdle to true");

        lock (_idleLock)
        {
            if (_idleTask is not null)
                return MailErrors.Internal(MailErrorCodes.ImapError, "IDLE already running — call StopIdle() first");
            _idleTask = Task.CompletedTask; // reserve; replaced below or cleared on failure
        }

        var started = false;
        try
        {
            int initialCount;
            using (await LockAsync(cancellationToken).ConfigureAwait(false))
            {
                var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

                if (!client.Capabilities.HasFlag(ImapCapabilities.Idle))
                {
                    _logger?.Warning("IMAP server does not support IDLE");
                    return MailErrors.Internal(MailErrorCodes.ImapError, "Server does not support IDLE");
                }

                var imapFolder = await GetImapFolderAsync(client, folder, FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
                if (imapFolder is null)
                    return MailErrors.NotFound(MailErrorCodes.FolderNotFound, $"Folder '{folder}' not found");
                initialCount = imapFolder.Count;
            }

            var stop = new CancellationTokenSource();
            var abort = new CancellationTokenSource();
            lock (_idleLock)
            {
                _idleCts = stop;
                _idleAbort = abort;
                _idleTask = Task.Run(() => IdleLoopAsync(folder, initialCount, stop.Token, abort.Token), CancellationToken.None);
            }
            started = true;

            _logger?.Info($"Started IDLE on '{folder}'");
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Fail(ex, "Error starting IDLE");
        }
        finally
        {
            if (!started)
                lock (_idleLock) { _idleTask = null; }
        }
    }

    private async Task IdleLoopAsync(string folder, int lastCount, CancellationToken stopToken, CancellationToken abortToken)
    {
        var refresh = TimeSpan.FromMinutes(_config.IdleRefreshMinutes);
        ImapClient? boundClient = null;
        IMailFolder? imapFolder = null;

        while (!stopToken.IsCancellationRequested)
        {
            int? newCount = null;
            try
            {
                await _gate.WaitAsync(stopToken).ConfigureAwait(false);
                try
                {
                    var client = await EnsureConnectedAsync(stopToken).ConfigureAwait(false);

                    // A reconnect replaces the client: the folder object of the old one is dead, so resolve it again.
                    if (imapFolder is null || !ReferenceEquals(boundClient, client))
                    {
                        imapFolder = await GetImapFolderAsync(client, folder, FolderAccess.None, stopToken).ConfigureAwait(false)
                            ?? throw new FolderNotFoundException(folder);
                        boundClient = client;
                    }

                    // A call on another folder closes this one (IMAP selects one folder at a time).
                    if (!imapFolder.IsOpen)
                        await imapFolder.OpenAsync(FolderAccess.ReadOnly, stopToken).ConfigureAwait(false);
                    newCount = CheckNewMail(imapFolder, ref lastCount);

                    if (newCount is null)
                    {
                        var done = new CancellationTokenSource();
                        Interlocked.Exchange(ref _idleDone, done);
                        try
                        {
                            // Only idle when no call is waiting for the connection (re-checked after publishing
                            // _idleDone, so a call that arrives now either sees it or is seen here).
                            if (Volatile.Read(ref _pendingCalls) == 0)
                            {
                                done.CancelAfter(refresh);
                                using var stopReg = stopToken.Register(static d => ((CancellationTokenSource)d!).Cancel(), done);
                                await client.IdleAsync(done.Token, abortToken).ConfigureAwait(false);
                            }
                        }
                        finally
                        {
                            Interlocked.Exchange(ref _idleDone, null);
                            done.Dispose();
                        }

                        newCount = CheckNewMail(imapFolder, ref lastCount);
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (OperationCanceledException) when (stopToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger?.Error($"IDLE error, retrying: {ex.Message}");
                imapFolder = null;
                try { await Task.Delay(5_000, stopToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            // Raised outside the lock, so a handler may call this service.
            if (newCount is { } count)
            {
                _logger?.Info($"New mail in '{folder}': {count} messages");
                try { NewMailReceived?.Invoke(this, new NewMailEventArgs { FolderName = folder, MessageCount = count }); }
                catch (Exception ex) { _logger?.Error("NewMailReceived handler threw", ex); }
            }
        }

        _logger?.Info("IDLE monitoring stopped");
    }

    private static int? CheckNewMail(IMailFolder folder, ref int lastCount)
    {
        var count = folder.Count;
        var grew = count > lastCount;
        lastCount = count;
        return grew ? count : null;
    }

    /// <summary>
    /// Stops the IDLE background loop and waits for it to complete (up to 5 s; an IDLE that does not end
    /// gracefully in that time is aborted).
    /// </summary>
    public void StopIdle()
    {
        CancellationTokenSource? stop, abort;
        Task? task;
        lock (_idleLock)
        {
            stop = _idleCts;
            abort = _idleAbort;
            task = _idleTask;
            _idleCts = null;
            _idleAbort = null;
            _idleTask = null;
        }
        if (stop is null) return;

        stop.Cancel();
        try
        {
            if (task is not null && !task.Wait(TimeSpan.FromSeconds(5)))
            {
                abort?.Cancel();
                task.Wait(TimeSpan.FromSeconds(2));
            }
        }
        catch (AggregateException) { }

        stop.Dispose();
        abort?.Dispose();
    }
}
