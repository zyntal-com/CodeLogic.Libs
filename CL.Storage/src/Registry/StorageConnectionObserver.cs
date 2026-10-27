using System.Collections.Concurrent;
using CL.Storage.Errors;
using CL.Storage.Events;
using CL.Storage.Models;
using CL.Storage.Providers;
using CodeLogic.Core.Results;

namespace CL.Storage.Registry;

/// <summary>Turns backend connection notifications into library events and log lines (failed operations are logged, throttled).</summary>
/// <remarks>
/// Backends are created before the library context is always available and outlive configuration
/// reloads, so the publisher is resolved per notification. Notifications are fire-and-forget: a slow or
/// failing event handler must never delay or fail the storage operation that raised it.
/// </remarks>
internal sealed class StorageConnectionObserver(Func<StorageEventPublisher?> publisher, Func<CodeLogic.Core.Logging.ILogger?> logger)
    : IStorageConnectionObserver
{
    public void SessionOpened(string connectionId, StorageProvider provider) =>
        Publish(new StorageConnectionOpenedEvent(connectionId, provider, DateTimeOffset.UtcNow));

    public void SessionFaulted(string connectionId, StorageProvider provider, string operation, Error error)
    {
        Log($"[Storage] {provider} connection '{connectionId}' lost its session during '{operation}' ({error.Code}); the session was retired.");
        Publish(new StorageConnectionLostEvent(connectionId, provider, operation, error.Code, DateTimeOffset.UtcNow));
    }

    public void Retrying(string connectionId, StorageProvider provider, string operation, int attempt, TimeSpan delay, Error error)
    {
        Log($"[Storage] Transient error on {provider} connection '{connectionId}' during '{operation}' ({error.Code}); retry {attempt} in {delay.TotalMilliseconds:0} ms.");
        Publish(new StorageConnectionRetryEvent(connectionId, provider, operation, attempt, delay, error.Code, DateTimeOffset.UtcNow));
    }

    public void OperationFailed(string connectionId, StorageProvider provider, string operation, string? path, Error error)
    {
        if (!IsRoutine(error.Code) && ShouldLog(connectionId, operation, error.Code, DateTimeOffset.UtcNow))
            Log($"[Storage] {provider} connection '{connectionId}': {operation} failed ({error.Code}): {error.Message}"
                + (string.IsNullOrEmpty(error.Details) ? string.Empty : $" [{error.Details}]"));
        Publish(new StorageOperationFailedEvent(connectionId, provider, operation, path, error.Code, DateTimeOffset.UtcNow));
    }

    /// <summary>Outcomes callers expect and handle (a missing file, a lost race, a cancel, a bad path): not logged.</summary>
    internal static bool IsRoutine(string code) =>
        code is StorageErrors.NotFoundCode or StorageErrors.ConflictCode or StorageErrors.CancelledCode or StorageErrors.InvalidPathCode;

    /// <summary>One log line per connection, operation and error code per <see cref="LogInterval"/>, so an outage does not flood the log.</summary>
    internal bool ShouldLog(string connectionId, string operation, string code, DateTimeOffset now)
    {
        var key = connectionId + "\u001f" + operation + "\u001f" + code;
        while (true)
        {
            if (!_lastLogged.TryGetValue(key, out var last))
            {
                if (_lastLogged.TryAdd(key, now)) return true;
                continue;
            }
            if (now - last < LogInterval) return false;
            if (_lastLogged.TryUpdate(key, now, last)) return true;
        }
    }

    /// <summary>Minimum time between two log lines for the same failure.</summary>
    internal static readonly TimeSpan LogInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastLogged = new(StringComparer.Ordinal);

    public void HealthChanged(string connectionId, StorageProvider provider, StorageConnectionHealth? previous, StorageConnectionHealth current)
    {
        if (!current.Healthy)
            Log($"[Storage] {provider} connection '{connectionId}' failed its health check ({current.ErrorCode}).");
        else if (previous is { Healthy: false })
            Log($"[Storage] {provider} connection '{connectionId}' is healthy again.");
        Publish(new StorageConnectionHealthChangedEvent(
            connectionId, provider, previous?.Healthy, current.Healthy, current.ErrorCode, current.Latency, current.CheckedAt));
    }

    private void Publish<TEvent>(TEvent @event) where TEvent : CodeLogic.Core.Events.IEvent
    {
        try
        {
            if (publisher() is { } target)
                _ = target.PublishAsync(@event);
        }
        catch { /* The library may be stopping; connection events are best-effort. */ }
    }

    private void Log(string message)
    {
        try { logger()?.Warning(message); }
        catch { /* Logging must not affect storage operations. */ }
    }
}
