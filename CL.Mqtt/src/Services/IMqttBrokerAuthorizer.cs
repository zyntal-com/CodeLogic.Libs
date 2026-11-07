using CL.Mqtt.Models;

namespace CL.Mqtt.Services;

/// <summary>
/// Decides who may connect to the embedded broker and which topics each client may use.
/// The default, <see cref="ConfigBrokerAuthorizer"/>, uses the users in <c>config.mqtt.json</c>. Replace it
/// (<see cref="MqttBrokerService.Authorizer"/>, before <see cref="MqttBrokerService.StartAsync"/>) to check
/// credentials against your own store or to hand out per-client permissions.
/// </summary>
/// <remarks>
/// <see cref="CanPublish"/> and <see cref="CanSubscribe"/> run for every publish and subscription, on the broker's
/// network threads: keep them fast and free of I/O. Do the expensive work in <see cref="AuthenticateAsync"/>.
/// </remarks>
public interface IMqttBrokerAuthorizer
{
    /// <summary>Decides whether a client may connect.</summary>
    /// <returns>True to accept the client; false to refuse it with "bad user name or password".</returns>
    ValueTask<bool> AuthenticateAsync(MqttBrokerConnectRequest request, CancellationToken cancellationToken);

    /// <summary>Decides whether a connected client may publish to <paramref name="topic"/>. A refused message is dropped.</summary>
    /// <param name="username">The user name the client connected with, or null when anonymous.</param>
    /// <param name="clientId">The client id.</param>
    /// <param name="topic">The topic name.</param>
    bool CanPublish(string? username, string clientId, string topic);

    /// <summary>Decides whether a connected client may subscribe to <paramref name="filter"/>. A refused subscription gets "not authorized".</summary>
    /// <param name="username">The user name the client connected with, or null when anonymous.</param>
    /// <param name="clientId">The client id.</param>
    /// <param name="filter">The topic filter, possibly with wildcards.</param>
    bool CanSubscribe(string? username, string clientId, string filter);
}
