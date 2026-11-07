using CL.Mqtt.Events;
using CL.Mqtt.Models;
using CodeLogic.Core.Events;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using MQTTnet;
using MQTTnet.Adapter;
using MQTTnet.Exceptions;
using MQTTnet.Formatter;

namespace CL.Mqtt.Services;

/// <summary>
/// One client connection to an MQTT broker.
/// <para>
/// <b>Connection.</b> <see cref="ConnectAsync"/> connects once and returns the outcome. With
/// <see cref="MqttConnectionConfig.AutoReconnect"/> (the default) a failed first attempt or a lost connection
/// is retried in the background with exponential back-off and jitter, until <see cref="DisconnectAsync"/>.
/// Refused credentials are retried too, but only at the longest delay: the broker is not hammered with a bad password.
/// </para>
/// <para>
/// <b>Subscriptions</b> are kept by the client, not only by the broker: a handler registered with
/// <see cref="SubscribeAsync(string, Func{MqttMessage, CancellationToken, Task}, MqttQos, CancellationToken)"/> keeps
/// working across reconnects, and may be registered before the first connect. Several handlers can share a filter;
/// the broker sees one subscription per filter.
/// </para>
/// <para>
/// <b>Handlers</b> run one after another on the client's receive loop, in the order messages arrive. Keep them
/// short or hand work off; a slow handler delays every later message. An exception in a handler is logged and
/// does not affect other handlers.
/// </para>
/// This file holds the connection lifecycle; <c>MqttClientService.Messaging.cs</c> holds publish and subscribe.
/// </summary>
public sealed partial class MqttClientService : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly IEventBus? _events;
    private readonly TimeProvider _time;
    private readonly IMqttClient _client;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<MqttSubscription> _subscriptions = [];

    private volatile bool _wantConnected;
    private volatile bool _disposed;
    private bool _everConnected;
    private Task? _reconnectLoop;
    private CancellationTokenSource? _reconnectCancel;
    private MqttConnectionConfig _config;

    /// <summary>Creates a client. It does not connect until <see cref="ConnectAsync"/> is called.</summary>
    /// <param name="name">Connection name, used in logs, events and errors.</param>
    /// <param name="config">Connection settings; validate them first (<see cref="MqttConnectionConfig.Validate"/>).</param>
    /// <param name="logger">Logger for connection changes and handler failures.</param>
    /// <param name="events">Optional event bus for <see cref="MqttConnectedEvent"/> and <see cref="MqttDisconnectedEvent"/>.</param>
    /// <param name="time">Clock for reconnect delays; <see cref="TimeProvider.System"/> when null.</param>
    public MqttClientService(string name, MqttConnectionConfig config, ILogger logger, IEventBus? events = null, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        Name = name;
        _config = config;
        _logger = logger;
        _events = events;
        _time = time ?? TimeProvider.System;
        ClientId = ResolveClientId(name, config);

        _client = new MqttClientFactory().CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
        _client.DisconnectedAsync += OnDisconnectedAsync;
    }

    /// <summary>Where the client is in its connection lifecycle.</summary>
    public MqttConnectionState State { get; private set; } = MqttConnectionState.Disconnected;
}
