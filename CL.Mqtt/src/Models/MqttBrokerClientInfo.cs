namespace CL.Mqtt.Models;

/// <summary>A client connected to the embedded broker, as returned by <c>MqttBrokerService.GetClientsAsync</c>.</summary>
/// <param name="ClientId">The client id the client connected with.</param>
/// <param name="Endpoint">The remote address and port.</param>
/// <param name="ConnectedAt">When the client connected (UTC).</param>
/// <param name="LastPacketReceivedAt">When the broker last heard from the client (UTC).</param>
/// <param name="MessagesReceived">Application messages the broker received from the client.</param>
/// <param name="MessagesSent">Application messages the broker sent to the client.</param>
public sealed record MqttBrokerClientInfo(
    string ClientId,
    string Endpoint,
    DateTimeOffset ConnectedAt,
    DateTimeOffset LastPacketReceivedAt,
    long MessagesReceived,
    long MessagesSent);
