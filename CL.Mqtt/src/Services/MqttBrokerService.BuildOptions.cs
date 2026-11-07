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

public sealed partial class MqttBrokerService
{


    // ── Internals ─────────────────────────────────────────────────────────────

    private static MqttServerOptions BuildOptions(MqttBrokerConfig config, out X509Certificate2? certificate)
    {
        certificate = null;
        var address = IPAddress.Parse(config.BindAddress);
        var builder = new MqttServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointPort(config.Port)
            .WithMaxPendingMessagesPerClient(config.MaxPendingMessagesPerClient)
            .WithPendingMessagesOverflowStrategy(MqttPendingMessagesOverflowStrategy.DropOldestQueuedMessage)
            .WithDefaultCommunicationTimeout(ConnectTimeout);

        // Listen on exactly the configured address. "0.0.0.0" also opens IPv6 ("::"), so the whole network is reachable
        // either way; a specific IPv4 address must not leave an IPv6 listener open behind it, and vice versa.
        var (v4, v6) = address.AddressFamily == AddressFamily.InterNetworkV6 ? (IPAddress.None, address)
            : address.Equals(IPAddress.Any) ? (IPAddress.Any, IPAddress.IPv6Any)
            : (address, IPAddress.None);
        builder.WithDefaultEndpointBoundIPAddress(v4).WithDefaultEndpointBoundIPV6Address(v6);

        if (config.Tls.Enabled)
        {
            certificate = config.Tls.LoadCertificate();
            builder.WithEncryptedEndpoint()
                .WithEncryptedEndpointPort(config.Tls.Port)
                .WithEncryptionCertificate(certificate)
                .WithEncryptionSslProtocol(SslProtocols.None)   // the OS default set of versions
                .WithEncryptedEndpointBoundIPAddress(v4)
                .WithEncryptedEndpointBoundIPV6Address(v6);
            if (config.Tls.DisablePlainPort)
                builder.WithoutDefaultEndpoint();
        }

        var options = builder.Build();
        options.EnablePersistentSessions = true;   // honour CleanSession = false from clients
        return options;
    }

    private async Task StopSweeperAsync()
    {
        var cancel = _sweepCancel;
        var sweeper = _sweeper;
        _sweepCancel = null;
        _sweeper = null;
        if (cancel is null || sweeper is null)
            return;
        await cancel.CancelAsync().ConfigureAwait(false);
        try
        {
            await sweeper.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }
        finally { cancel.Dispose(); }
    }

    private async Task DisposeRetainedAsync()
    {
        if (_retained is not null)
        {
            await _retained.DisposeAsync().ConfigureAwait(false);
            _retained = null;
        }
    }

    private void RemoveTap(BrokerTap tap)
    {
        lock (_lock)
            _taps.Remove(tap);
    }

    /// <summary>Who holds a client id, and since when its client has been away (null while connected).</summary>
    private sealed record SessionOwner(string? Username, string Endpoint, Guid Generation,
        DateTimeOffset ReservedAt, DateTimeOffset? DisconnectedAt = null, bool Pending = true, bool Deleting = false,
        SessionOwner? Previous = null);

    /// <summary>A registered tap; disposing it removes it.</summary>
    private sealed class BrokerTap(MqttBrokerService owner, string filter, Func<MqttMessage, CancellationToken, Task> handler) : IDisposable
    {
        public string Filter { get; } = filter;

        public Func<MqttMessage, CancellationToken, Task> Handler { get; } = handler;

        public void Dispose() => owner.RemoveTap(this);
    }
}
