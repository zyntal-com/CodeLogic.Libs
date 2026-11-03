using CodeLogic.Core.Results;

namespace CL.Mqtt.Models;

/// <summary>
/// Stable <see cref="Error.Code"/> values for every failure CL.Mqtt reports.
/// Each code always comes with the same <see cref="Error"/> kind, listed on the constant.
/// </summary>
public static class MqttErrorCodes
{
    /// <summary>No connection with that name is configured. Kind: <see cref="NotFoundError"/>.</summary>
    public const string ConnectionNotFound = "mqtt.connection_not_found";

    /// <summary>
    /// The operation needs a live connection to the broker, and the client is not connected (yet, or any more).
    /// Kind: <see cref="UnavailableError"/>.
    /// </summary>
    public const string NotConnected = "mqtt.not_connected";

    /// <summary>
    /// The broker could not be reached, or it refused the connection for a reason other than credentials.
    /// Kind: <see cref="UnavailableError"/>; <see cref="Error.Details"/> carries the MQTT reason code when there is one.
    /// </summary>
    public const string ConnectFailed = "mqtt.connect_failed";

    /// <summary>The broker rejected the user name or password, or the client is not authorized. Kind: <see cref="UnauthorizedError"/>.</summary>
    public const string AuthenticationFailed = "mqtt.authentication_failed";

    /// <summary>Connecting, publishing or subscribing took longer than the configured timeout. Kind: <see cref="TimeoutError"/>.</summary>
    public const string Timeout = "mqtt.timeout";

    /// <summary>
    /// A topic name to publish to is empty, too long, contains a wildcard (<c>+</c>, <c>#</c>) or a NUL character.
    /// Kind: <see cref="ValidationError"/>.
    /// </summary>
    public const string InvalidTopic = "mqtt.invalid_topic";

    /// <summary>
    /// A topic filter to subscribe to is empty, too long, contains a NUL character, or uses a wildcard in the wrong
    /// place (<c>#</c> must be the last level, <c>+</c> must fill a whole level). Kind: <see cref="ValidationError"/>.
    /// </summary>
    public const string InvalidFilter = "mqtt.invalid_filter";

    /// <summary>
    /// The broker refused a publish (MQTT 5 only; MQTT 3.1.1 brokers drop it silently).
    /// Kind: <see cref="ValidationError"/>; <see cref="Error.Details"/> carries the MQTT reason code.
    /// </summary>
    public const string PublishRejected = "mqtt.publish_rejected";

    /// <summary>
    /// The broker refused a subscription, typically because the client may not read that topic.
    /// Kind: <see cref="UnauthorizedError"/>; <see cref="Error.Details"/> carries the MQTT reason code.
    /// </summary>
    public const string SubscribeRejected = "mqtt.subscribe_rejected";

    /// <summary>A payload could not be read or written as JSON. Kind: <see cref="ValidationError"/>.</summary>
    public const string InvalidJson = "mqtt.invalid_json";

    /// <summary>The embedded broker is not running, so it cannot publish or list clients. Kind: <see cref="UnavailableError"/>.</summary>
    public const string BrokerNotRunning = "mqtt.broker_not_running";

    /// <summary>
    /// The embedded broker could not start, usually because its port is in use or not allowed.
    /// Kind: <see cref="UnavailableError"/>; <see cref="Error.Details"/> carries the underlying message.
    /// </summary>
    public const string BrokerStartFailed = "mqtt.broker_start_failed";

    /// <summary>The client or broker was disposed. Kind: <see cref="UnavailableError"/>.</summary>
    public const string Disposed = "mqtt.disposed";

    /// <summary>The operation was cancelled by the caller. Kind: <see cref="UnavailableError"/>.</summary>
    public const string Cancelled = "mqtt.cancelled";

    /// <summary>An unexpected error. Kind: <see cref="InternalError"/>.</summary>
    public const string Unknown = "mqtt.unknown";
}

/// <summary>Builds CL.Mqtt errors so every code is always paired with the same kind.</summary>
internal static class MqttErrors
{
    public static Error ConnectionNotFound(string name) =>
        Error.NotFound(MqttErrorCodes.ConnectionNotFound, $"MQTT connection '{name}' is not configured.");

    public static Error NotConnected(string name) =>
        Error.Unavailable(MqttErrorCodes.NotConnected, $"MQTT connection '{name}' is not connected.");

    public static Error ConnectFailed(string name, string message, string? details = null) =>
        Error.Unavailable(MqttErrorCodes.ConnectFailed, $"MQTT connection '{name}' failed: {message}", details);

    public static Error AuthenticationFailed(string name, string? details = null) =>
        Error.Unauthorized(MqttErrorCodes.AuthenticationFailed, $"The broker refused the credentials of MQTT connection '{name}'.", details);

    public static Error Timeout(string name, string operation) =>
        Error.Timeout(MqttErrorCodes.Timeout, $"MQTT connection '{name}': {operation} timed out.");

    public static Error InvalidTopic(string message) => Error.Validation(MqttErrorCodes.InvalidTopic, message);

    public static Error InvalidFilter(string message) => Error.Validation(MqttErrorCodes.InvalidFilter, message);

    public static Error PublishRejected(string topic, string reasonCode, string? reason) =>
        Error.Validation(MqttErrorCodes.PublishRejected, $"The broker refused the publish to '{topic}'{(string.IsNullOrEmpty(reason) ? "" : $": {reason}")}.", reasonCode);

    public static Error SubscribeRejected(string filter, string reasonCode) =>
        Error.Unauthorized(MqttErrorCodes.SubscribeRejected, $"The broker refused the subscription to '{filter}'.", reasonCode);

    public static Error InvalidJson(string message, Exception? ex = null) =>
        Error.Validation(MqttErrorCodes.InvalidJson, message, ex?.Message);

    public static Error BrokerNotRunning() =>
        Error.Unavailable(MqttErrorCodes.BrokerNotRunning, "The embedded MQTT broker is not running.");

    public static Error BrokerStartFailed(int port, Exception ex) =>
        Error.Unavailable(MqttErrorCodes.BrokerStartFailed, $"The embedded MQTT broker could not start on port {port}.", ex.Message);

    public static Error Disposed(string what) => Error.Unavailable(MqttErrorCodes.Disposed, $"{what} has been disposed.");

    public static Error Cancelled() => Error.Unavailable(MqttErrorCodes.Cancelled, "Operation cancelled");

    /// <summary>An <see cref="InternalError"/> describing <paramref name="ex"/>.</summary>
    public static Error Unknown(Exception ex) => Error.Internal(MqttErrorCodes.Unknown, ex.Message, ex.GetType().FullName);
}
