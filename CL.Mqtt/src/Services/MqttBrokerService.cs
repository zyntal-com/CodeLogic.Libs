using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using CL.Mqtt.Events;
using CL.Mqtt.Models;
using CodeLogic.Core.Events;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;

namespace CL.Mqtt.Services;

/// <summary>
/// The embedded MQTT broker: an MQTT server inside the application process.
/// <para>
/// <b>Security.</b> Every connection, publish and subscription goes through <see cref="Authorizer"/>
/// (by default <see cref="ConfigBrokerAuthorizer"/>, the users in <c>config.mqtt.json</c>). On top of that the broker
/// itself refuses client ids starting with <c>$</c>, binds every client id to the user that first connected with it
/// (another user cannot take the session over), caps the sessions a user may hold and the retained messages it may
/// store, and expires persistent sessions nobody reconnects to. The default bind address is <c>127.0.0.1</c>, so only
/// local programs can connect until you open it up; do that with <see cref="MqttBrokerConfig.Tls"/> on.
/// </para>
/// <para>
/// <b>Taps</b> (<see cref="Tap"/>) let the application see messages clients publish without connecting a client
/// of its own; <see cref="PublishAsync(string, ReadOnlyMemory{byte}, MqttPublishOptions?, CancellationToken)"/>
/// publishes as the broker itself.
/// </para>
/// <para>
/// <b>Retained messages</b> are kept in a file when <see cref="MqttBrokerConfig.PersistRetainedMessages"/> is on and
/// a file path was given, so devices that publish their state retained are known again after a restart.
/// </para>
/// </summary>
public sealed partial class MqttBrokerService : IAsyncDisposable
{
    /// <summary>The client id messages published by the broker itself carry.</summary>
    public const string BrokerClientId = "$broker";

    /// <summary>How often persistent sessions are checked for expiry.</summary>
    internal static readonly TimeSpan SessionSweepInterval = TimeSpan.FromMinutes(1);

    /// <summary>How long a client has to send CONNECT after the TCP connection opened.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    private readonly ILogger _logger;
    private readonly IEventBus? _events;
    private readonly string? _retainedFile;
    private readonly Lock _lock = new();
    private readonly List<BrokerTap> _taps = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    // Identifies the broker's own injected messages in the publish interceptor, whatever client id they carry.
    private readonly IDictionary _brokerSessionItems = new Dictionary<object, object>();
    private readonly Lock _sessionLock = new();
    private readonly object _sessionKey = new();
    private readonly Dictionary<string, SessionOwner> _sessions = new(StringComparer.Ordinal);

    private HashSet<string> _retainedTopics = new(StringComparer.Ordinal);
    private MqttServer? _server;
    private RetainedMessageStore? _retained;
    private X509Certificate2? _certificate;
    private CancellationTokenSource? _sweepCancel;
    private Task? _sweeper;
    private IMqttBrokerAuthorizer _authorizer;

    /// <summary>Creates the broker. It does not listen until <see cref="StartAsync"/>.</summary>
    /// <param name="config">Broker settings; validate them first (<see cref="MqttBrokerConfig.Validate"/>).</param>
    /// <param name="logger">Logger for start/stop, refused clients and tap failures.</param>
    /// <param name="events">Optional event bus for the broker events.</param>
    /// <param name="retainedFile">File for retained messages; null keeps them in memory only.</param>
    public MqttBrokerService(MqttBrokerConfig config, ILogger logger, IEventBus? events = null, string? retainedFile = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        Config = config;
        _logger = logger;
        _events = events;
        _retainedFile = retainedFile;
        _authorizer = new ConfigBrokerAuthorizer(config);
    }
}
