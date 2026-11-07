using CodeLogic.Core.Events;

namespace CL.Mqtt.Events;

/// <summary>A client connection reached its broker (first connect or reconnect). Published on the CodeLogic event bus.</summary>
/// <param name="ConnectionName">The connection name from <c>config.mqtt.json</c> (or the name given to <c>CreateClient</c>).</param>
/// <param name="ClientId">The client id used.</param>
/// <param name="IsReconnect">True when the connection was lost before and has now been restored.</param>
public sealed record MqttConnectedEvent(string ConnectionName, string ClientId, bool IsReconnect) : IEvent;

/// <summary>A client connection to its broker was lost or closed. Published on the CodeLogic event bus.</summary>
/// <param name="ConnectionName">The connection name.</param>
/// <param name="Reason">Why, as reported by the broker or the network stack.</param>
/// <param name="WillReconnect">True when the client keeps retrying in the background.</param>
public sealed record MqttDisconnectedEvent(string ConnectionName, string Reason, bool WillReconnect) : IEvent;

/// <summary>The embedded broker started listening.</summary>
/// <param name="Endpoint">The address and port it listens on.</param>
public sealed record MqttBrokerStartedEvent(string Endpoint) : IEvent;

/// <summary>The embedded broker stopped.</summary>
public sealed record MqttBrokerStoppedEvent : IEvent;

/// <summary>A client connected to the embedded broker.</summary>
/// <param name="ClientId">The client id.</param>
/// <param name="Username">The user name, or null for an anonymous client.</param>
/// <param name="Endpoint">The remote address and port.</param>
public sealed record MqttBrokerClientConnectedEvent(string ClientId, string? Username, string Endpoint) : IEvent;

/// <summary>A client left the embedded broker.</summary>
/// <param name="ClientId">The client id.</param>
/// <param name="Username">The user name, or null for an anonymous client.</param>
/// <param name="Clean">True when the client disconnected properly; false when the connection dropped (its last will is published).</param>
public sealed record MqttBrokerClientDisconnectedEvent(string ClientId, string? Username, bool Clean) : IEvent;

/// <summary>The embedded broker refused a client. Useful as a security signal (repeated attempts).</summary>
/// <param name="ClientId">The client id the client sent.</param>
/// <param name="Username">The user name it sent, or null.</param>
/// <param name="Endpoint">The remote address and port.</param>
/// <param name="Reason">
/// Why: <c>credentials</c> (user name or password), <c>reserved client id</c> (an id starting with <c>$</c>),
/// <c>client id held by …</c> (another user holds that session) or <c>session limit of N reached</c>.
/// </param>
public sealed record MqttBrokerConnectionRejectedEvent(string ClientId, string? Username, string Endpoint, string Reason = "credentials") : IEvent;
