using CL.Mail.Models;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using System.Net.Sockets;

namespace CL.Mail.Services;

/// <summary>
/// Reads and manages e-mail via IMAP using <see href="https://github.com/jstedfast/MailKit">MailKit</see>.
/// Supports message fetching, search, flag operations, folder management, and RFC 2177 IDLE push.
/// <para>
/// One IMAP connection serves every call. Calls are serialized, so the service is safe to use from several
/// threads; while IDLE is running, a call interrupts it, runs, and IDLE resumes afterwards.
/// </para>
/// <para>
/// The class is split by concern: this file holds the connection, the lock and the error mapping;
/// <c>ImapService.Folders.cs</c> the folder operations, <c>ImapService.Messages.cs</c> fetching and
/// manipulating messages, <c>ImapService.Search.cs</c> search, and <c>ImapService.Idle.cs</c> IDLE push.
/// </para>
/// </summary>
public sealed partial class ImapService : IDisposable
{
    private readonly ImapConfig _config;
    private readonly ILogger? _logger;
    private ImapClient? _client;
    private volatile bool _disposed;

    // Serializes every use of _client: public calls and the IDLE loop.
    private readonly SemaphoreSlim _gate = new(1, 1);
    // Public calls waiting for (or holding) the gate; the IDLE loop does not start an IDLE while any are waiting.
    private int _pendingCalls;
    // Ends the IDLE command in progress gracefully (DONE) so a waiting call can take the connection.
    private CancellationTokenSource? _idleDone;

    /// <summary>Initialises the IMAP service with the given configuration.</summary>
    /// <param name="config">IMAP server settings.</param>
    /// <param name="logger">Optional logger.</param>
    public ImapService(ImapConfig config, ILogger? logger = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;
    }

    // ── Connection ────────────────────────────────────────────────────────────

    /// <summary>
    /// Connects and authenticates to the IMAP server. An existing connection is closed and replaced.
    /// </summary>
    public async Task<Result> ConnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var _ = await LockAsync(cancellationToken).ConfigureAwait(false);
            await ConnectCoreAsync(cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Fail(ex, "IMAP connection error");
        }
    }

    /// <summary>Gracefully disconnects from the IMAP server and stops IDLE if running.</summary>
    public async Task<Result> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            StopIdle();
            using var _ = await LockAsync(cancellationToken).ConfigureAwait(false);
            if (_client is { IsConnected: true })
            {
                await _client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
                _logger?.Info("Disconnected from IMAP");
            }
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Fail(ex, "Error disconnecting from IMAP");
        }
    }

    // ── Connection helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Takes the connection for one call. A running IDLE is ended gracefully first, and resumes when the call
    /// releases the connection.
    /// </summary>
    private async Task<Releaser> LockAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Interlocked.Increment(ref _pendingCalls);
        try
        {
            var done = Volatile.Read(ref _idleDone);
            if (done is not null)
            {
                try { done.Cancel(); }
                catch (ObjectDisposedException) { /* the IDLE already ended */ }
            }
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _pendingCalls);
        }
        return new Releaser(_gate);
    }

    private readonly struct Releaser(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    /// <summary>Opens a new connection, replacing (and disposing) the current one. Caller holds the gate.</summary>
    private async Task<ImapClient> ConnectCoreAsync(CancellationToken cancellationToken)
    {
        var security = MapSecurity(_config.SecurityMode);

        var previous = _client;
        _client = null;
        if (previous is not null)
        {
            try
            {
                if (previous.IsConnected)
                    await previous.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.Debug($"Closing the previous IMAP connection failed: {ex.Message}");
            }
            finally
            {
                previous.Dispose();
            }
        }

        var client = new ImapClient { Timeout = _config.TimeoutSeconds * 1000 };
        try
        {
            await client.ConnectAsync(_config.Host, _config.Port, security, cancellationToken).ConfigureAwait(false);
            await client.AuthenticateAsync(_config.Username, _config.Password, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        _client = client;
        _logger?.Info($"Connected to IMAP {_config.Host}:{_config.Port}");
        return client;
    }

    /// <summary>Returns a connected, authenticated client, reconnecting when needed. Caller holds the gate.</summary>
    private async Task<ImapClient> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_client is { IsConnected: true, IsAuthenticated: true } client) return client;
        if (_client is not null) _logger?.Info("IMAP reconnecting...");
        return await ConnectCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IMailFolder?> GetImapFolderAsync(ImapClient client, string name, FolderAccess access, CancellationToken ct)
    {
        IMailFolder? folder;
        try
        {
            folder = string.Equals(name, "INBOX", StringComparison.OrdinalIgnoreCase)
                ? client.Inbox
                : await client.GetFolderAsync(name, ct).ConfigureAwait(false);
        }
        catch (FolderNotFoundException) { return null; }

        if (folder is null) return null;

        if (access != FolderAccess.None)
        {
            if (!folder.IsOpen)
                await folder.OpenAsync(access, ct).ConfigureAwait(false);
            else if (folder.Access < access)
            {
                await folder.CloseAsync(false, ct).ConfigureAwait(false);
                await folder.OpenAsync(access, ct).ConfigureAwait(false);
            }
        }

        return folder;
    }

    // ── Error mapping ─────────────────────────────────────────────────────────

    /// <summary>
    /// Maps an exception from an IMAP operation to an <see cref="Error"/> of the matching kind and logs it.
    /// Server answers (<c>NO</c>/<c>BAD</c> text, missing folder or message names) are returned; the text of an
    /// unexpected exception is logged only.
    /// </summary>
    private Error Fail(Exception ex, string what)
    {
        switch (ex)
        {
            case OperationCanceledException:
                _logger?.Warning($"{what}: cancelled");
                return MailErrors.Cancelled();
            case ImapConfigException:
                _logger?.Error($"{what}: {ex.Message}");
                return MailErrors.Validation(MailErrorCodes.ImapConfigInvalid, ex.Message);
            case AuthenticationException:
                _logger?.Error($"{what}: authentication failed", ex);
                return MailErrors.Unauthorized(MailErrorCodes.ImapAuthenticationFailed, "Authentication failed");
            case TimeoutException:
                _logger?.Error($"{what}: timed out", ex);
                return MailErrors.Timeout(MailErrorCodes.ImapTimeout, "IMAP operation timed out");
            case SocketException or IOException or ServiceNotConnectedException or SslHandshakeException:
                _logger?.Error($"{what}: IMAP server {_config.Host}:{_config.Port} unreachable", ex);
                return MailErrors.Unavailable(MailErrorCodes.ImapConnectionFailed,
                    $"Could not connect to IMAP server {_config.Host}:{_config.Port}", ex);
            case FolderNotFoundException:
                _logger?.Warning($"{what}: {ex.Message}");
                return MailErrors.NotFound(MailErrorCodes.FolderNotFound, ex.Message);
            case MessageNotFoundException:
                _logger?.Warning($"{what}: {ex.Message}");
                return MailErrors.NotFound(MailErrorCodes.MessageNotFound, ex.Message);
            case ImapCommandException command:
                _logger?.Warning($"{what}: server answered {command.Response}: {command.ResponseText}");
                return MailErrors.Validation(MailErrorCodes.ImapRejected, ex.Message, $"IMAP {command.Response}");
            default:
                _logger?.Error(what, ex);
                return MailErrors.Internal(MailErrorCodes.ImapError, $"{what}: an unexpected error occurred", ex);
        }
    }

    /// <summary>
    /// Maps the configured security mode. An unknown value fails closed: it is a configuration error, never a
    /// silent fallback to opportunistic TLS.
    /// </summary>
    internal static SecureSocketOptions MapSecurity(MailSecurityMode mode) => mode switch
    {
        MailSecurityMode.None     => SecureSocketOptions.None,
        MailSecurityMode.StartTls => SecureSocketOptions.StartTls,
        MailSecurityMode.SslTls   => SecureSocketOptions.SslOnConnect,
        _                         => throw new ImapConfigException($"Imap.SecurityMode '{mode}' is not a known security mode")
    };

    private sealed class ImapConfigException(string message) : Exception(message);

    // ── IDisposable ───────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopIdle();
        _client?.Dispose();
        _client = null;
    }
}
