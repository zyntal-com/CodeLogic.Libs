using System.Collections;
using CL.Mqtt.Models;

namespace CL.Mqtt.Services;

public sealed partial class MqttBrokerService
{
    internal static bool HasNetworkPlainListener(MqttBrokerConfig config) =>
        !config.IsLoopback && !(config.Tls.Enabled && config.Tls.DisablePlainPort);

    // Ownership, admission and quota accounting form one atomic operation, including connections not yet established.
    internal bool TryReserveSession(string id, string? username, string endpoint, DateTimeOffset now,
        out Guid generation, out string reason)
    {
        lock (_sessionLock)
        {
            generation = default;
            var owner = _sessions.GetValueOrDefault(id);
            if (owner is not null && !string.Equals(owner.Username, username, StringComparison.Ordinal))
            {
                reason = $"client id held by {(owner.Username is null ? "an anonymous client" : $"user '{owner.Username}'")}";
                return false;
            }
            if (owner is { Pending: true } or { Deleting: true })
            {
                reason = "client id is being established or expired; retry later";
                return false;
            }
            var cap = Config.MaxSessionsPerUser;
            if (owner is null && cap > 0 && _sessions.Count(s => string.Equals(s.Value.Username, username, StringComparison.Ordinal)) >= cap)
            {
                reason = $"session limit of {cap} reached";
                return false;
            }
            generation = Guid.NewGuid();
            _sessions[id] = new SessionOwner(username, endpoint, generation, now, Previous: owner);
            reason = "";
            return true;
        }
    }

    private Guid SessionGeneration(IDictionary items) => items[_sessionKey] is Guid value ? value : Guid.Empty;

    private void SessionConnected(string id, string endpoint, IDictionary items)
    {
        lock (_sessionLock)
        {
            if (_sessions.TryGetValue(id, out var owner) && owner.Generation == SessionGeneration(items) && owner.Endpoint == endpoint)
                _sessions[id] = owner with { Pending = false, DisconnectedAt = null, Previous = null };
        }
    }

    private void SessionDisconnected(string id, string endpoint, IDictionary items)
    {
        lock (_sessionLock)
        {
            if (_sessions.TryGetValue(id, out var owner) && !owner.Pending &&
                owner.Generation == SessionGeneration(items) && owner.Endpoint == endpoint)
                _sessions[id] = owner with { DisconnectedAt = DateTimeOffset.UtcNow };
        }
    }

    private void SessionDeleted(string id, IDictionary items)
    {
        lock (_sessionLock)
        {
            // A clean-session replacement may delete the previous session while a new connection is pending.
            if (_sessions.TryGetValue(id, out var owner) && !owner.Pending && !owner.Deleting && owner.Generation == SessionGeneration(items))
                _sessions.Remove(id);
        }
    }

    private async Task SweepLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(SessionSweepInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                try { await SweepSessionsAsync(DateTimeOffset.UtcNow).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { _logger.Error($"MQTT broker: session sweep failed: {ex}"); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    /// <summary>Expires idle sessions, preserving pending reservations and connections established during a sweep.</summary>
    internal async Task SweepSessionsAsync(DateTimeOffset now)
    {
        var server = _server;
        if (server is null) return;
        KeyValuePair<string, SessionOwner>[] candidates;
        lock (_sessionLock) candidates = [.. _sessions];
        var connected = (await server.GetClientsAsync().ConfigureAwait(false)).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var existing = (await server.GetSessionsAsync().ConfigureAwait(false)).ToDictionary(s => s.Id, StringComparer.Ordinal);
        var expiry = Config.PersistentSessionExpiryMinutes > 0 ? TimeSpan.FromMinutes(Config.PersistentSessionExpiryMinutes) : (TimeSpan?)null;

        foreach (var (id, candidate) in candidates)
        {
            existing.TryGetValue(id, out var session);
            SessionOwner deleting;
            lock (_sessionLock)
            {
                if (!_sessions.TryGetValue(id, out var current) || !ReferenceEquals(current, candidate)) continue;
                if (current.Pending)
                {
                    // Failed replacement handshakes restore the previous owner, rather than holding it pending forever.
                    if (now - current.ReservedAt < ConnectTimeout * 2) continue;
                    if (session is null) _sessions.Remove(id);
                    else if (SessionGeneration(session.Items) == current.Generation)
                        _sessions[id] = current with { Pending = false, Previous = null,
                            DisconnectedAt = connected.Contains(id) ? null : now };
                    else if (current.Previous is { } previous)
                        _sessions[id] = previous with { DisconnectedAt = connected.Contains(id) ? null : previous.DisconnectedAt ?? now };
                    continue;
                }
                if (connected.Contains(id)) continue;
                if (current.Deleting || current.DisconnectedAt is null) continue;
                if (session is null) { _sessions.Remove(id); continue; }
                if (expiry is not { } limit || now - current.DisconnectedAt.Value < limit) continue;
                // Admission is blocked until deletion completes, so a reconnect cannot race DeleteAsync.
                deleting = current with { Deleting = true };
                _sessions[id] = deleting;
            }
            try
            {
                await session!.DeleteAsync().ConfigureAwait(false);
                lock (_sessionLock)
                    if (_sessions.TryGetValue(id, out var current) && ReferenceEquals(current, deleting)) _sessions.Remove(id);
                _logger.Debug($"MQTT broker: expired the persistent session of '{CL.Internal.LogText.Sanitize(id)}'");
            }
            catch
            {
                lock (_sessionLock)
                    if (_sessions.TryGetValue(id, out var current) && ReferenceEquals(current, deleting)) _sessions[id] = candidate;
                throw;
            }
        }
    }
}
