# CL.Mqtt

> MQTT clients with automatic reconnect, and an embedded MQTT broker — built on MQTTnet.

`CL.Mqtt` adds MQTT to a CodeLogic application. Configure any number of named client connections in `config.mqtt.json`;
each one connects at start, reconnects by itself and keeps its subscriptions across reconnects. Optionally run an MQTT
broker inside the application, with user accounts, topic permissions and retained messages that survive a restart.
Every fallible operation returns `Result` / `Result<T>` (namespace `CodeLogic.Core.Results`) whose `Error.Code` is one of
`MqttErrorCodes`; network and protocol failures are returned, not thrown.

| | |
|---|---|
| **Package** | [`CodeLogic.Mqtt`](https://www.nuget.org/packages/CodeLogic.Mqtt) |
| **Library class** | `CL.Mqtt.MqttLibrary` |
| **Config file** | `config.mqtt.json` |
| **Data file** | `retained.json` (broker retained messages, in the library's data directory) |
| **Dependencies** | MQTTnet 5.x · MQTTnet.Server 5.x |

## Install & load

```bash
dotnet add package CodeLogic.Mqtt
```

```csharp
using CL.Mqtt;

await Libraries.LoadAsync<MqttLibrary>();   // register before ConfigureAsync()
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

var mqtt = Libraries.Get<MqttLibrary>();
```

**What each phase does:**
- **First run:** writes `config.mqtt.json` with one `Default` connection to `localhost:1883` and the broker off.
- **Initialize:** creates the clients and the broker, but connects nothing yet. That makes the gap between Initialize and Start the place to:
  - register subscriptions;
  - replace the broker's authorizer.
- **Start:**
  1. starts the broker first;
  2. then connects every connection with `autoConnect`, in parallel.
- **Start failures:**
  - **Broker:** a broker that cannot listen (port in use) fails the start.
  - **Unreachable broker for a client:** does not fail the start. The client keeps retrying and the health check reports `Degraded`.

## Library surface

| Member | Returns | Purpose |
|--------|---------|---------|
| `Client()` | `Result<MqttClientService>` | The default connection (`defaultConnection`). |
| `Client(name)` | `Result<MqttClientService>` | A configured connection by name (case-insensitive); `mqtt.connection_not_found` otherwise. |
| `Clients` | `IReadOnlyDictionary<string, MqttClientService>` | Every configured connection. |
| `Broker` | `MqttBrokerService` | The embedded broker — **throws** when `broker.enabled` is false. |
| `HasBroker` | `bool` | `true` when the broker is enabled. |
| `CreateClient(name, config)` | `MqttClientService` | A client outside the configuration; the caller connects and disposes it. |

The services also work without the CodeLogic runtime (tests, tools): `new MqttClientService(name, config, logger)` and
`new MqttBrokerService(config, logger)`.

## Clients

### Connecting

```csharp
var client = mqtt.Client().ValueOrThrow();

var connected = await client.ConnectAsync();   // only needed when autoConnect is false
if (connected.IsFailure)
    logger.Warning($"MQTT: {connected.Error!.Code} {connected.Error.Message}");   // still retrying when autoReconnect
```

**How the client behaves:**
- **`ConnectAsync`:** makes one attempt and returns its outcome.
  - With `autoReconnect` (the default), a failed attempt keeps retrying in the background.
  - A connection lost later is retried the same way.
- **Back-off:** the delay starts at `reconnectMinDelaySeconds` and doubles up to `reconnectMaxDelaySeconds`, with ±20 % jitter so a fleet of clients does not retry in step.
- **Logging:** the first failed retry is logged, then every tenth.
- **`DisconnectAsync`:** disconnects cleanly and stops retrying. Subscriptions are kept for the next connect.

**State you can read:** `State` (`Disconnected`, `Connecting`, `Connected`, `Reconnecting`), `IsConnected`,
`ConnectedSince`, `FailedAttempts`, `LastError`, `ClientId`.

**Client id:** when `clientId` is empty the client uses `cl-{machine}-{connection}`. Two clients of the same user with
the same id disconnect each other, so give every process its own. Ids starting with `$` are reserved by brokers.

**Persistent sessions:** `cleanSession: false` needs a fixed `clientId`. On `V500` the client also sends
`sessionExpirySeconds` (1 day by default); MQTT 5 drops a session at disconnect without it.

### Subscribing

```csharp
var subscription = (await client.SubscribeAsync("home/+/temperature", async (message, ct) =>
{
    var reading = message.ReadJson<Reading>();
    if (reading.IsSuccess)
        await store.SaveAsync(message.Topic, reading.Value!, ct);
}, MqttQos.AtLeastOnce)).ValueOrThrow();

await subscription.DisposeAsync();   // stop; unsubscribes when no other handler uses the filter
```

**How subscriptions behave:**
- **Ownership:** subscriptions belong to the client.
  - The client sends them again on every connect, so they survive reconnects and broker restarts.
  - You can register them before the first connect.
- **Sharing:** several handlers can share a filter. The broker sees one subscription per filter, at the highest QoS any handler asked for.
- **Errors:**
  - While connected, the broker is asked first. A refusal (for example a permission) returns `mqtt.subscribe_rejected` and the handler is not registered.
  - A malformed filter returns `mqtt.invalid_filter`.
- **Handler order:** handlers run one after another on the client's receive loop, in arrival order.
  - Keep them short, or hand work to a queue.
  - An exception in a handler is logged and does not stop other handlers.
  - The `CancellationToken` is cancelled when the client is disposed.
- **Message copies:** the `MqttMessage` a handler receives is a copy, safe to keep after the handler returns.

**Topic filters:** `+` matches exactly one level, `#` matches the rest. Topics that start with `$` (such as `$SYS/…`)
are not matched by a leading wildcard. `MqttTopic.Matches(topic, filter)` applies the same rules in your own code.

### Publishing

```csharp
await client.PublishAsync("home/kitchen/temperature", "21.5");                         // UTF-8 text
await client.PublishAsync("cameras/door/snapshot", jpegBytes);                          // bytes
await client.PublishJsonAsync("home/kitchen/state", state, options: MqttPublishOptions.Retained);
await client.PublishJsonAsync("home/kitchen/state", state, AppJson.Default.LampState); // source-generated (AOT)
```

**`MqttPublishOptions`:**

| Option | Default | Notes |
|---|---|---|
| `Qos` | `AtLeastOnce` | `AtMostOnce` (0), `AtLeastOnce` (1), `ExactlyOnce` (2). |
| `Retain` | `false` | The broker keeps the message as the topic's last value. An empty retained payload clears it. `MqttPublishOptions.Retained` is QoS 1 + retain. |
| `ContentType` | — | MQTT 5. `PublishJsonAsync` sets `application/json` unless you set another. |
| `MessageExpiry` | — | MQTT 5. The broker drops the message if it cannot deliver it in time. |
| `ResponseTopic`, `CorrelationData` | — | MQTT 5 request/response. |
| `UserProperties` | — | MQTT 5 key/value pairs. |

MQTT 5 options are ignored on an MQTT 3.1.1 connection (`protocol: "V311"`, the default).

**When the publish completes:**
- **QoS 1/2:** `PublishAsync` completes when the broker has acknowledged the message.
- **QoS 0:** it completes when the message has been sent.
- **While disconnected:** it returns `mqtt.not_connected` at once. Nothing is queued, so the caller decides whether a message is worth retrying.

### Reconfiguring

`client.ReconfigureAsync(newConfig)` disconnects, applies the new settings and reconnects, and keeps every
subscription. The library uses it for live config changes, so code holding a client keeps working.

## Embedded broker

```json
"broker": {
  "enabled": true,
  "port": 1883,
  "bindAddress": "0.0.0.0",
  "tls": { "enabled": true, "port": 8883, "certificatePath": "/etc/app/broker.pfx", "certificatePassword": "…", "disablePlainPort": true },
  "allowAnonymous": false,
  "maxSessionsPerUser": 100,
  "persistentSessionExpiryMinutes": 1440,
  "persistRetainedMessages": true,
  "maxPendingMessagesPerClient": 1000,
  "maxRetainedMessages": 10000,
  "maxRetainedPayloadBytes": 1048576,
  "users": [
    { "username": "sensor", "password": "…", "publishTopics": ["sensors/#"], "subscribeTopics": ["commands/sensor/+"], "clientIds": ["sensor-1"] },
    { "username": "dashboard", "password": "$pbkdf2-sha256$600000$…$…", "canPublish": false, "subscribeTopics": ["home/#"] },
    { "username": "admin",  "password": "…" }
  ]
}
```

**Security model:**
- **Listening address:** the broker listens on exactly `bindAddress`.
  - The default `127.0.0.1` accepts local programs only, and no IPv6 listener is opened behind it.
  - `0.0.0.0` accepts the whole network on IPv4 and IPv6. Turn `allowAnonymous` off, configure users and turn `tls` on before you open it: without TLS, passwords and payloads travel in clear, and the broker logs a warning at start.
- **TLS** (`tls`): a PKCS#12 certificate with its private key, served on `tls.port` (8883). `disablePlainPort` closes the plain listener once every client has moved. The certificate is validated with the configuration; a certificate that cannot be loaded fails the start with `mqtt.broker_start_failed`.
- **Anonymous clients** (no user name) are accepted only with `allowAnonymous`, and may then use every topic.
- **Users:**
  - A user must match `username` and `password`. The password is either clear text or a hash made with `MqttBrokerUser.HashPassword` (PBKDF2-SHA256, 600 000 iterations, `$pbkdf2-sha256$…`), so it need not sit in the configuration file. Both are compared in constant time, and an unknown user takes as long as a wrong password.
  - `clientIds` pins the account to the listed client ids (empty allows any), so a leaked device password cannot be used from elsewhere.
  - `canPublish` / `canSubscribe` (default true) forbid a direction entirely. With them on, `publishTopics` limits where a user may publish (a message to another topic is dropped; MQTT 3.1.1 gives the publisher no error, MQTT 5 does) and `subscribeTopics` limits what it may subscribe to (other subscriptions are refused with "not authorized"). A requested filter is allowed when a permission **covers** it:
    - `home/#` covers `home/+/state`;
    - `home/+` does not cover `home/#`.
  - **Empty lists allow everything**; use `canPublish: false` for a read-only account.
- **Client ids and sessions** (enforced by the broker, whatever the authorizer):
  - Ids starting with `$` are refused (`$broker` is the broker's own, see below).
  - A client id belongs to the user that first connected with it, for as long as its session exists: another user is refused with "client identifier not valid", so it can neither resume the session (and inherit its subscriptions and queued messages) nor kick the client off. The same user may reconnect with the id at any time.
  - `maxSessionsPerUser` (100, 0 for no limit) caps the sessions a user holds, connected or persistent and offline: MQTT 5 clients get "quota exceeded", MQTT 3.1.1 clients "server unavailable".
  - `persistentSessionExpiryMinutes` (1 day, 0 to keep until stop) deletes persistent sessions whose client has been away that long; MQTT 3.1.1 has no expiry of its own.
- **Retained messages:** `maxRetainedMessages` (10 000) and `maxRetainedPayloadBytes` (1 MiB) bound what clients can store. Beyond them a retained publish is dropped (MQTT 5: "quota exceeded"); updates of existing topics and clears (empty payload) always pass.
- **Refused connections** are logged and published as `MqttBrokerConnectionRejectedEvent` with a `Reason`, a useful signal for repeated attempts.

### Your own accounts

Implement `IMqttBrokerAuthorizer` and set it between Initialize and Start (or any time; it applies to the next
connection, publish and subscription):

```csharp
public sealed class DeviceAuthorizer(DeviceStore devices) : IMqttBrokerAuthorizer
{
    public async ValueTask<bool> AuthenticateAsync(MqttBrokerConnectRequest request, CancellationToken ct) =>
        request.Username is { } user && await devices.CheckTokenAsync(user, request.Password, ct);

    // Called for every message and subscription: keep these fast and free of I/O.
    public bool CanPublish(string? username, string clientId, string topic) =>
        username is not null && topic.StartsWith($"devices/{username}/", StringComparison.Ordinal);

    public bool CanSubscribe(string? username, string clientId, string filter) =>
        username is not null && MqttTopic.Covers($"commands/{username}/#", filter);
}

mqtt.Broker.Authorizer = new DeviceAuthorizer(devices);
```

### Taps, publishing and clients

```csharp
using var tap = mqtt.Broker.Tap("devices/#", async (message, ct) =>
{
    // message.SenderClientId / SenderUserName say who published it.
    await ingest.WriteAsync(message, ct);
}).ValueOrThrow();

await mqtt.Broker.PublishAsync("commands/lamp-1/set", "on");      // sent as client id "$broker"

var clients = (await mqtt.Broker.GetClientsAsync()).ValueOrThrow();  // id, endpoint, connected at, counters
```

**How taps work:**
- **What they see:** messages that clients publish, after the authorizer allowed them, without connecting a client of your own. They do not see the broker's own `PublishAsync` (recognised by its session, not by the `$broker` id, which no client can claim).
- **Where they run:** on the publishing client's connection, so keep them short.

### Retained messages

With `persistRetainedMessages` (the default), the broker keeps retained messages in `retained.json` in the library's data
directory and loads them at start. Devices that publish their state retained are therefore known again right after a
restart. Writes are batched (one writer, at most one write every two seconds, plus one at stop) and atomic. The caps
`maxRetainedMessages` and `maxRetainedPayloadBytes` apply to what clients store, not to the broker's own publishes.

### Queues

`maxPendingMessagesPerClient` (1000) is how many messages the broker queues for a slow or offline client; beyond it the
oldest queued message is dropped. A client has 15 seconds to send CONNECT after the TCP connection opened.

## Configuration reference

`config.mqtt.json`:

| Setting | Default | Meaning |
|---|---|---|
| `enabled` | `true` | Master switch. |
| `defaultConnection` | `"Default"` | Connection returned by `Client()`. Must exist when any connection is configured. |
| `connections` | `{ "Default": … }` | Named connections (case-insensitive). |
| `broker` | disabled | The embedded broker (see above). |

**Each connection:**

| Setting | Default | Meaning |
|---|---|---|
| `enabled` | `true` | Turn the connection off without removing it. |
| `host`, `port` | `localhost`, `1883` | The broker. |
| `useTls`, `allowUntrustedCertificates` | `false`, `false` | TLS; accepting untrusted certificates is for test setups only. |
| `protocol` | `V311` | `V311` or `V500`. |
| `username`, `password` | — | Credentials (the password is a secret: masked in the admin schema). |
| `clientId` | generated | Required for `cleanSession: false`; cannot start with `$`. |
| `cleanSession` | `true` | `false` keeps subscriptions and queued messages at the broker while away. |
| `sessionExpirySeconds` | `86400` | MQTT 5 only: how long the broker keeps the session after a disconnect (sent with `cleanSession: false`). |
| `keepAliveSeconds` | `30` | Ping interval. |
| `timeoutSeconds` | `10` | Connect, publish and subscribe timeout. |
| `autoConnect` | `true` | Connect at start. |
| `autoReconnect` | `true` | Retry transient failures. Authentication refusal stops retries; correct credentials and call `ConnectAsync` explicitly. |
| `reconnectMinDelaySeconds`, `reconnectMaxDelaySeconds` | `1`, `60` | Back-off range. |
| `willTopic`, `willPayload`, `willRetain` | — | Last will, published by the broker when the client vanishes. |

**Live changes** (admin UI, file watcher) apply without a restart:
- **Connections:**
  - an added connection is created and connected;
  - a removed one is disposed;
  - a changed one is reconfigured in place, keeping its subscriptions.
- **Broker:**
  - user, anonymous-access and limit changes apply at once;
  - port, address, TLS and storage changes restart the broker, and clients reconnect on their own.
- **Invalid changes:** a change that fails validation is logged and ignored.

## Events

Published on the CodeLogic event bus (`context.Events` / `CodeLogic.GetEventBus()`):

| Event | When |
|---|---|
| `MqttConnectedEvent(ConnectionName, ClientId, IsReconnect)` | A client connected (first time or after a loss). |
| `MqttDisconnectedEvent(ConnectionName, Reason, WillReconnect)` | A client lost or closed its connection. |
| `MqttBrokerStartedEvent(Endpoint)` / `MqttBrokerStoppedEvent` | The broker started or stopped. |
| `MqttBrokerClientConnectedEvent(ClientId, Username, Endpoint)` | A client connected to the broker. |
| `MqttBrokerClientDisconnectedEvent(ClientId, Username, Clean)` | A client left; `Clean = false` means it dropped (its last will is published). |
| `MqttBrokerConnectionRejectedEvent(ClientId, Username, Endpoint, Reason)` | The broker refused a client: `credentials`, `reserved client id`, `client id held by …` or `session limit of N reached`. |

## Error codes

| Code | Kind | When |
|---|---|---|
| `mqtt.connection_not_found` | NotFound | `Client(name)` for an unknown name. |
| `mqtt.not_connected` | Unavailable | Publish while not connected. |
| `mqtt.connect_failed` | Unavailable | Broker unreachable or refused for a reason other than credentials. |
| `mqtt.authentication_failed` | Unauthorized | Bad user name or password, or not authorized. |
| `mqtt.timeout` | Timeout | Connect, publish or subscribe took longer than `timeoutSeconds`. |
| `mqtt.invalid_topic` | Validation | Empty, too long, wildcard or NUL in a topic to publish to. |
| `mqtt.invalid_filter` | Validation | Malformed subscription filter. |
| `mqtt.publish_rejected` | Validation | MQTT 5 broker refused a publish (`Details` = reason code). |
| `mqtt.subscribe_rejected` | Unauthorized | Broker refused a subscription (`Details` = reason code). |
| `mqtt.invalid_json` | Validation | Payload not readable/writable as JSON. |
| `mqtt.broker_not_running` | Unavailable | Broker operation while stopped. |
| `mqtt.broker_start_failed` | Unavailable | Broker could not listen or load its TLS certificate (`Details` = cause). |
| `mqtt.disposed` | Unavailable | Client or broker was disposed (for example removed by a config change). |
| `mqtt.cancelled` | Unavailable | The caller's token was cancelled. |
| `mqtt.unknown` | Internal | Unexpected failure. |

## Health

**What `HealthCheckAsync` reports:**
- **Healthy:** the broker (if enabled) runs and every `autoConnect` connection is connected.
- **Degraded:** a connection is down. The message names it and its last error, and it keeps retrying.
- **Unhealthy:** the broker stopped after start.

## Escape hatches

`MqttClientService.RawClient` (MQTTnet `IMqttClient`) and `MqttBrokerService.RawServer` (MQTTnet `MqttServer`) expose
features the wrapper does not cover, such as enhanced authentication or packet inspection. Do not connect, disconnect or
subscribe through them: the wrapper would not know.

`MqttLibrary` is `IAsyncDisposable`; a normal `StopAsync` already tears everything down, so `Dispose()` after it returns at once.

## Admission and shutdown guarantees

Client-id ownership checks, quota accounting and handshake reservations are atomic. A pending connection counts against
its user's session quota. Sweeping preserves pending/replaced sessions and blocks reconnects while an expired session is
being deleted. Failed handshakes release their reservation after the CONNECT timeout grace period.

Authentication refusal stops automatic reconnect; after correcting credentials call `ConnectAsync` explicitly. Transient
network failures retain exponential backoff. MQTTnet 5.2 does not expose a result property on its connect-failure exception,
so the adapter extracts known reason-code names from the exception message.

Enabling TLS does not close the plain port: set `tls.disablePlainPort: true` to require encryption. A reachable plain
listener emits a warning even when a TLS listener is also enabled.

`Dispose()` starts cleanup without waiting on asynchronous work. Await `DisposeAsync()` or the runtime's normal
`OnStopAsync()` path when shutdown must be complete (disconnects, retained-message flush and resource release).
