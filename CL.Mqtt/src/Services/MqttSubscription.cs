using CL.Mqtt.Models;

namespace CL.Mqtt.Services;

/// <summary>
/// A handler registered with <see cref="MqttClientService.SubscribeAsync(string, Func{MqttMessage, CancellationToken, Task}, MqttQos, CancellationToken)"/>.
/// Dispose it to stop receiving; the client unsubscribes from the broker once no handler uses the filter any more.
/// The subscription survives reconnects: the client subscribes again every time it connects.
/// </summary>
public sealed class MqttSubscription : IAsyncDisposable
{
    private readonly MqttClientService _owner;
    private int _disposed;

    internal MqttSubscription(MqttClientService owner, string filter, MqttQos qos, Func<MqttMessage, CancellationToken, Task> handler)
    {
        _owner = owner;
        Filter = filter;
        Qos = qos;
        Handler = handler;
    }

    /// <summary>The topic filter.</summary>
    public string Filter { get; }

    /// <summary>The QoS requested for this handler.</summary>
    public MqttQos Qos { get; }

    /// <summary>True until the subscription is disposed.</summary>
    public bool IsActive => Volatile.Read(ref _disposed) == 0;

    internal Func<MqttMessage, CancellationToken, Task> Handler { get; }

    /// <summary>Stops the handler and unsubscribes from the broker when this was the filter's last handler.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            await _owner.RemoveAsync(this).ConfigureAwait(false);
    }
}
