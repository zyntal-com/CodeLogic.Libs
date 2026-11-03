using System.Security.Cryptography;
using System.Text;
using CL.Mqtt.Models;

namespace CL.Mqtt.Services;

/// <summary>
/// The default broker authorizer: accounts and topic permissions from <see cref="MqttBrokerConfig"/>.
/// <list type="bullet">
///   <item><description>A client without a user name is accepted only when <see cref="MqttBrokerConfig.AllowAnonymous"/> is on,
///     and may then use every topic.</description></item>
///   <item><description>A client with a user name must match a configured user and password (compared in constant time).</description></item>
///   <item><description>A user's <see cref="MqttBrokerUser.PublishTopics"/> and <see cref="MqttBrokerUser.SubscribeTopics"/> limit
///     what it may use; an empty list allows everything.</description></item>
/// </list>
/// </summary>
public sealed class ConfigBrokerAuthorizer : IMqttBrokerAuthorizer
{
    private volatile MqttBrokerConfig _config;

    /// <summary>Creates the authorizer for a broker configuration.</summary>
    public ConfigBrokerAuthorizer(MqttBrokerConfig config) =>
        _config = config ?? throw new ArgumentNullException(nameof(config));

    /// <summary>Switches to new settings (users, anonymous access). Connected clients keep their connection.</summary>
    public void Update(MqttBrokerConfig config) =>
        _config = config ?? throw new ArgumentNullException(nameof(config));

    /// <inheritdoc/>
    public ValueTask<bool> AuthenticateAsync(MqttBrokerConnectRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var config = _config;
        if (string.IsNullOrEmpty(request.Username))
            return ValueTask.FromResult(config.AllowAnonymous);

        var user = FindUser(config, request.Username);
        // Compare against a dummy when the user is unknown, so the answer takes as long either way.
        var expected = user?.Password ?? "\0no-such-user\0";
        var matches = PasswordEquals(expected, request.Password ?? "") && user is not null;
        return ValueTask.FromResult(matches);
    }

    /// <inheritdoc/>
    public bool CanPublish(string? username, string clientId, string topic)
    {
        if (string.IsNullOrEmpty(username))
            return _config.AllowAnonymous;
        var user = FindUser(_config, username);
        return user is not null && (user.PublishTopics.Count == 0 || user.PublishTopics.Any(f => MqttTopic.Matches(topic, f)));
    }

    /// <inheritdoc/>
    public bool CanSubscribe(string? username, string clientId, string filter)
    {
        if (string.IsNullOrEmpty(username))
            return _config.AllowAnonymous;
        var user = FindUser(_config, username);
        return user is not null && (user.SubscribeTopics.Count == 0 || user.SubscribeTopics.Any(f => MqttTopic.Covers(f, filter)));
    }

    private static MqttBrokerUser? FindUser(MqttBrokerConfig config, string username) =>
        config.Users.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.Ordinal));

    /// <summary>Constant-time comparison of the SHA-256 hashes, so length and content leak nothing through timing.</summary>
    private static bool PasswordEquals(string expected, string actual) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
            SHA256.HashData(Encoding.UTF8.GetBytes(actual)));
}
