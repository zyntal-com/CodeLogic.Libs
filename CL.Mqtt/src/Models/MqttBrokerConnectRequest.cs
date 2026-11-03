namespace CL.Mqtt.Models;

/// <summary>A client asking the embedded broker to let it in, as passed to <c>IMqttBrokerAuthorizer.AuthenticateAsync</c>.</summary>
/// <param name="ClientId">The client id the client sent.</param>
/// <param name="Username">The user name, or null when the client sent none.</param>
/// <param name="Password">The password, or null when the client sent none.</param>
/// <param name="Endpoint">The remote address and port.</param>
/// <param name="IsSecureConnection">True when the client connected over TLS.</param>
public sealed record MqttBrokerConnectRequest(string ClientId, string? Username, string? Password, string Endpoint, bool IsSecureConnection)
{
    /// <summary>Keeps the password out of logs and debugger views.</summary>
    public override string ToString() => $"{ClientId} ({Username ?? "anonymous"}) from {Endpoint}";
}
