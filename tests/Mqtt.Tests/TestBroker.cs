using System.Net;
using System.Net.Sockets;
using CL.Mqtt.Models;
using CL.Mqtt.Services;
using CodeLogic.Core.Events;
using CodeLogic.Core.Logging;

namespace Mqtt.Tests;

/// <summary>An embedded broker on a free loopback port, plus helpers to create clients for it.</summary>
internal sealed class TestBroker : IAsyncDisposable
{
    private readonly List<MqttClientService> _clients = [];

    private TestBroker(MqttBrokerService broker, int port, EventBus events)
    {
        Broker = broker;
        Port = port;
        Events = events;
    }

    public MqttBrokerService Broker { get; }

    public int Port { get; }

    public EventBus Events { get; }

    public static async Task<TestBroker> StartAsync(Action<MqttBrokerConfig>? configure = null, string? retainedFile = null, int? port = null)
    {
        var chosen = port ?? FreePort();
        var config = new MqttBrokerConfig { Enabled = true, Port = chosen, BindAddress = "127.0.0.1", AllowAnonymous = true };
        configure?.Invoke(config);
        var events = new EventBus();
        var broker = new MqttBrokerService(config, NullLogger.Instance, events, retainedFile);
        (await broker.StartAsync()).ThrowIfFailure();
        return new TestBroker(broker, chosen, events);
    }

    /// <summary>A client for this broker (fast reconnect, short timeouts), disposed with the broker.</summary>
    public MqttClientService Client(string name = "test", Action<MqttConnectionConfig>? configure = null)
    {
        var config = new MqttConnectionConfig
        {
            Host = "127.0.0.1",
            Port = Port,
            ClientId = $"{name}-{Guid.NewGuid():N}",
            TimeoutSeconds = 5,
            ReconnectMinDelaySeconds = 1,
            ReconnectMaxDelaySeconds = 2
        };
        configure?.Invoke(config);
        var client = new MqttClientService(name, config, NullLogger.Instance, Events);
        _clients.Add(client);
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
            await client.DisposeAsync();
        await Broker.DisposeAsync();
    }

    public static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

/// <summary>Collects messages and lets a test wait for a count.</summary>
internal sealed class Inbox
{
    private readonly Lock _lock = new();
    private readonly List<MqttMessage> _messages = [];
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<MqttMessage> Messages
    {
        get { lock (_lock) return [.. _messages]; }
    }

    public void Add(MqttMessage message)
    {
        TaskCompletionSource changed;
        lock (_lock)
        {
            _messages.Add(message);
            changed = _changed;
            _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        changed.TrySetResult();
    }

    public Task Handle(MqttMessage message, CancellationToken _)
    {
        Add(message);
        return Task.CompletedTask;
    }

    /// <summary>Waits until at least <paramref name="count"/> messages arrived; fails the test after <paramref name="seconds"/>.</summary>
    public async Task<IReadOnlyList<MqttMessage>> WaitForAsync(int count, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            Task changed;
            lock (_lock)
            {
                if (_messages.Count >= count)
                    return [.. _messages];
                changed = _changed.Task;
            }
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || await Task.WhenAny(changed, Task.Delay(left)) != changed)
                throw new TimeoutException($"Expected {count} message(s), got {Messages.Count}.");
        }
    }

    /// <summary>Waits a little and checks that no more than <paramref name="count"/> messages arrived.</summary>
    public async Task ExpectNoMoreThanAsync(int count, int milliseconds = 500)
    {
        await Task.Delay(milliseconds);
        if (Messages.Count > count)
            throw new InvalidOperationException($"Expected at most {count} message(s), got {Messages.Count}.");
    }
}
