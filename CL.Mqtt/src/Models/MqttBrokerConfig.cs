using System.Net;
using CodeLogic.Core.Configuration;

namespace CL.Mqtt.Models;

/// <summary>
/// Settings of the embedded MQTT broker (<see cref="MqttConfig.Broker"/>). The broker runs inside the
/// application process; other programs and devices connect to it like to any MQTT broker.
/// </summary>
public sealed class MqttBrokerConfig
{
    /// <summary>Run the embedded broker. Off by default.</summary>
    [ConfigField(Label = "Enabled", Group = "Broker", Order = 0)]
    public bool Enabled { get; set; }

    /// <summary>TCP port to listen on (1883 is the standard MQTT port).</summary>
    [ConfigField(Label = "Port", Min = 1, Max = 65535, Group = "Broker", Order = 1)]
    public int Port { get; set; } = 1883;

    /// <summary>
    /// IP address to listen on. <c>127.0.0.1</c> (the default) accepts only local programs; <c>0.0.0.0</c>
    /// accepts the whole network, so set <see cref="AllowAnonymous"/> to false, configure users and turn on
    /// <see cref="Tls"/> first.
    /// </summary>
    [ConfigField(Label = "Bind Address", Placeholder = "127.0.0.1", Group = "Broker", Order = 2)]
    public string BindAddress { get; set; } = "127.0.0.1";

    /// <summary>The TLS listener. Off by default; required before the broker is opened to a network.</summary>
    [ConfigField(Label = "TLS", Group = "Broker", Order = 3)]
    public MqttBrokerTlsConfig Tls { get => field; set => field = value ?? new(); } = new();

    /// <summary>
    /// Accept clients that send no user name. Anonymous clients may publish and subscribe to every topic.
    /// </summary>
    [ConfigField(Label = "Allow Anonymous", Group = "Security", Order = 10)]
    public bool AllowAnonymous { get; set; }

    /// <summary>Accounts that may connect, with the topics each may publish and subscribe to.</summary>
    [ConfigField(Label = "Users", Group = "Security", Order = 11)]
    public List<MqttBrokerUser> Users { get => field; set => field = value ?? []; } = [];

    /// <summary>
    /// Sessions (connected or persistent and offline) one user may hold at a time; 0 for no limit. A client id
    /// belongs to the user that first connected with it until its session ends, so the limit is also what bounds
    /// the memory a user can tie up in persistent sessions.
    /// </summary>
    [ConfigField(Label = "Max Sessions Per User", Min = 0, Max = 100_000, Group = "Security", Order = 12, Collapsed = true)]
    public int MaxSessionsPerUser { get; set; } = 100;

    /// <summary>
    /// Minutes a persistent session (<c>cleanSession: false</c>) is kept after its client disconnected; 0 keeps it
    /// until the broker stops. MQTT 3.1.1 has no session expiry of its own, so without this a session that never
    /// reconnects would queue messages forever.
    /// </summary>
    [ConfigField(Label = "Persistent Session Expiry (min)", Min = 0, Max = 527_040, Group = "Security", Order = 13, Collapsed = true)]
    public int PersistentSessionExpiryMinutes { get; set; } = 1440;

    /// <summary>
    /// Keep retained messages in a file in the library's data directory, so they survive a restart.
    /// Without it, retained messages live in memory only.
    /// </summary>
    [ConfigField(Label = "Persist Retained Messages", Group = "Storage", Order = 20)]
    public bool PersistRetainedMessages { get; set; } = true;

    /// <summary>
    /// Messages queued per client before the oldest queued message is dropped (a slow or offline client with a
    /// persistent session must not grow memory without bound).
    /// </summary>
    [ConfigField(Label = "Max Pending Messages Per Client", Min = 10, Max = 1_000_000, Group = "Storage", Order = 21, Collapsed = true)]
    public int MaxPendingMessagesPerClient { get; set; } = 1000;

    /// <summary>
    /// Retained messages the broker keeps (one per topic). A retained publish to a new topic beyond this is dropped
    /// (MQTT 5 publishers get "quota exceeded"); updates and clears of existing topics always go through.
    /// </summary>
    [ConfigField(Label = "Max Retained Messages", Min = 1, Max = 10_000_000, Group = "Storage", Order = 22, Collapsed = true)]
    public int MaxRetainedMessages { get; set; } = 10_000;

    /// <summary>Largest payload a retained message may have, in bytes; larger retained publishes are dropped.</summary>
    [ConfigField(Label = "Max Retained Payload (bytes)", Min = 1, Max = 268_435_455, Group = "Storage", Order = 23, Collapsed = true)]
    public int MaxRetainedPayloadBytes { get; set; } = 1_048_576;

    /// <summary>Checks the broker settings; <paramref name="path"/> prefixes every error path.</summary>
    public IEnumerable<ConfigFieldError> Validate(string path)
    {
        if (Port is < 1 or > 65535)
            yield return new($"{path}.port", "Port must be between 1 and 65535.");
        if (!IPAddress.TryParse(BindAddress, out _))
            yield return new($"{path}.bindAddress", $"'{BindAddress}' is not an IP address.");
        if (MaxPendingMessagesPerClient is < 10 or > 1_000_000)
            yield return new($"{path}.maxPendingMessagesPerClient", "MaxPendingMessagesPerClient must be between 10 and 1000000.");
        if (MaxSessionsPerUser is < 0 or > 100_000)
            yield return new($"{path}.maxSessionsPerUser", "MaxSessionsPerUser must be between 0 and 100000.");
        if (PersistentSessionExpiryMinutes is < 0 or > 527_040)
            yield return new($"{path}.persistentSessionExpiryMinutes", "PersistentSessionExpiryMinutes must be between 0 and 527040.");
        if (MaxRetainedMessages is < 1 or > 10_000_000)
            yield return new($"{path}.maxRetainedMessages", "MaxRetainedMessages must be between 1 and 10000000.");
        if (MaxRetainedPayloadBytes is < 1 or > 268_435_455)
            yield return new($"{path}.maxRetainedPayloadBytes", "MaxRetainedPayloadBytes must be between 1 and 268435455.");
        foreach (var error in Tls.Validate($"{path}.tls"))
            yield return error;
        if (Tls.Enabled && !Tls.DisablePlainPort && Tls.Port == Port)
            yield return new($"{path}.tls.port", "The TLS port must differ from the plain port.");

        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Users.Count; i++)
        {
            var user = Users[i];
            if (user is null || string.IsNullOrWhiteSpace(user.Username))
            {
                yield return new($"{path}.users[{i}].username", "Every broker user needs a user name.");
                continue;
            }
            if (user.Username.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
                yield return new($"{path}.users[{i}].username", $"User '{user.Username}': the user name cannot contain spaces or control characters.");
            if (user.Username.StartsWith('$'))
                yield return new($"{path}.users[{i}].username", $"User '{user.Username}': names starting with '$' are reserved.");
            if (!names.Add(user.Username))
                yield return new($"{path}.users[{i}].username", $"User '{user.Username}' is listed twice.");
            if (string.IsNullOrEmpty(user.Password))
                yield return new($"{path}.users[{i}].password", $"User '{user.Username}' needs a password.");
            else if (MqttBrokerUser.IsHash(user.Password) && !MqttBrokerUser.TryParseHash(user.Password, out _, out _, out _))
                yield return new($"{path}.users[{i}].password", $"User '{user.Username}': the password hash is malformed; make it with MqttBrokerUser.HashPassword.");
            foreach (var filter in user.PublishTopics.Concat(user.SubscribeTopics))
            {
                if (Services.MqttTopic.ValidateFilter(filter) is { } error)
                    yield return new($"{path}.users[{i}]", $"User '{user.Username}': {error}");
            }
            foreach (var clientId in user.ClientIds)
            {
                if (string.IsNullOrWhiteSpace(clientId) || clientId.StartsWith('$') || clientId.Any(char.IsControl))
                    yield return new($"{path}.users[{i}].clientIds", $"User '{user.Username}': '{clientId}' is not a usable client id.");
            }
        }
    }

    /// <summary>True when the listener settings (what the broker binds and keeps) are the same; user changes do not count.</summary>
    internal bool ListenerEquals(MqttBrokerConfig other) =>
        Port == other.Port
        && string.Equals(BindAddress, other.BindAddress, StringComparison.Ordinal)
        && PersistRetainedMessages == other.PersistRetainedMessages
        && MaxPendingMessagesPerClient == other.MaxPendingMessagesPerClient
        && Tls.ListenerEquals(other.Tls);

    /// <summary>True when <see cref="BindAddress"/> is a loopback address.</summary>
    internal bool IsLoopback => IPAddress.TryParse(BindAddress, out var address) && IPAddress.IsLoopback(address);
}
