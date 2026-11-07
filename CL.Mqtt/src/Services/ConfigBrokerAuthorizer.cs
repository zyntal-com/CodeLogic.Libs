using CL.Mqtt.Models;

namespace CL.Mqtt.Services;

/// <summary>
/// The default broker authorizer: accounts and topic permissions from <see cref="MqttBrokerConfig"/>.
/// <list type="bullet">
///   <item><description>A client without a user name is accepted only when <see cref="MqttBrokerConfig.AllowAnonymous"/> is on,
///     and may then use every topic.</description></item>
///   <item><description>A client with a user name must match a configured user and password (clear or hashed, compared in
///     constant time), and one of the user's <see cref="MqttBrokerUser.ClientIds"/> when any are listed.</description></item>
///   <item><description>A user's <see cref="MqttBrokerUser.CanPublish"/> / <see cref="MqttBrokerUser.PublishTopics"/> and
///     <see cref="MqttBrokerUser.CanSubscribe"/> / <see cref="MqttBrokerUser.SubscribeTopics"/> limit what it may use;
///     an empty list allows everything.</description></item>
/// </list>
/// The broker itself binds every client id to the user that holds it, so this class does not have to.
/// </summary>
public sealed class ConfigBrokerAuthorizer : IMqttBrokerAuthorizer
{
    // A hashed dummy, so an unknown user costs the same PBKDF2 work as a wrong password when hashes are in use.
    private static readonly Lazy<string> DummyHash = new(() => MqttBrokerUser.HashPassword("\0no-such-user\0"));
    private const string DummyClear = "\0no-such-user\0";

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
        var expected = user?.Password ?? (config.Users.Any(u => u.HasHashedPassword) ? DummyHash.Value : DummyClear);
        var matches = MqttBrokerUser.PasswordMatches(expected, request.Password ?? "") & user is not null;
        if (matches && user!.ClientIds.Count > 0 && !user.ClientIds.Contains(request.ClientId, StringComparer.Ordinal))
            matches = false;
        return ValueTask.FromResult(matches);
    }

    /// <inheritdoc/>
    public bool CanPublish(string? username, string clientId, string topic)
    {
        if (string.IsNullOrEmpty(username))
            return _config.AllowAnonymous;
        var user = FindUser(_config, username);
        return user is { CanPublish: true } && (user.PublishTopics.Count == 0 || user.PublishTopics.Any(f => MqttTopic.Matches(topic, f)));
    }

    /// <inheritdoc/>
    public bool CanSubscribe(string? username, string clientId, string filter)
    {
        if (string.IsNullOrEmpty(username))
            return _config.AllowAnonymous;
        var user = FindUser(_config, username);
        return user is { CanSubscribe: true } && (user.SubscribeTopics.Count == 0 || user.SubscribeTopics.Any(f => MqttTopic.Covers(f, filter)));
    }

    private static MqttBrokerUser? FindUser(MqttBrokerConfig config, string username) =>
        config.Users.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.Ordinal));
}
