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
    /// accepts the whole network, so set <see cref="AllowAnonymous"/> to false and configure users first.
    /// </summary>
    [ConfigField(Label = "Bind Address", Placeholder = "127.0.0.1", Group = "Broker", Order = 2)]
    public string BindAddress { get; set; } = "127.0.0.1";

    /// <summary>
    /// Accept clients that send no user name. Anonymous clients may publish and subscribe to every topic.
    /// </summary>
    [ConfigField(Label = "Allow Anonymous", Group = "Security", Order = 10)]
    public bool AllowAnonymous { get; set; }

    /// <summary>Accounts that may connect, with the topics each may publish and subscribe to.</summary>
    [ConfigField(Label = "Users", Group = "Security", Order = 11)]
    public List<MqttBrokerUser> Users { get; set; } = [];

    /// <summary>
    /// Keep retained messages in a file in the library's data directory, so they survive a restart.
    /// Without it, retained messages live in memory only.
    /// </summary>
    [ConfigField(Label = "Persist Retained Messages", Group = "Storage", Order = 20)]
    public bool PersistRetainedMessages { get; set; } = true;

    /// <summary>
    /// Messages queued per client before the oldest are dropped (a slow or offline client with a persistent
    /// session must not grow memory without bound).
    /// </summary>
    [ConfigField(Label = "Max Pending Messages Per Client", Min = 10, Max = 1_000_000, Group = "Storage", Order = 21, Collapsed = true)]
    public int MaxPendingMessagesPerClient { get; set; } = 1000;

    /// <summary>Checks the broker settings; <paramref name="path"/> prefixes every error path.</summary>
    public IEnumerable<ConfigFieldError> Validate(string path)
    {
        if (Port is < 1 or > 65535)
            yield return new($"{path}.port", "Port must be between 1 and 65535.");
        if (!IPAddress.TryParse(BindAddress, out _))
            yield return new($"{path}.bindAddress", $"'{BindAddress}' is not an IP address.");
        if (MaxPendingMessagesPerClient is < 10 or > 1_000_000)
            yield return new($"{path}.maxPendingMessagesPerClient", "MaxPendingMessagesPerClient must be between 10 and 1000000.");

        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < (Users?.Count ?? 0); i++)
        {
            var user = Users![i];
            if (user is null || string.IsNullOrWhiteSpace(user.Username))
            {
                yield return new($"{path}.users[{i}].username", "Every broker user needs a user name.");
                continue;
            }
            if (!names.Add(user.Username))
                yield return new($"{path}.users[{i}].username", $"User '{user.Username}' is listed twice.");
            if (string.IsNullOrEmpty(user.Password))
                yield return new($"{path}.users[{i}].password", $"User '{user.Username}' needs a password.");
            foreach (var filter in user.PublishTopics.Concat(user.SubscribeTopics))
            {
                if (Services.MqttTopic.ValidateFilter(filter) is { } error)
                    yield return new($"{path}.users[{i}]", $"User '{user.Username}': {error}");
            }
        }
    }
}
