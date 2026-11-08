# CL.Mqtt — Changelog

All notable changes to **CodeLogic.Mqtt** are documented here. Versions follow
[Semantic Versioning](https://semver.org/).

## 5.0 (unreleased)

### Changed

- Capture the sweeper's cancellation token before scheduling so an immediate stop cannot clear its source first. Broker listeners and cancellation sources are released even if a sweeper fails during shutdown; deferred-start and faulted-sweep regressions cover both paths.

- Make session ownership and quotas atomic, protect pending/replaced sessions during sweeping, warn for every network-visible plain listener, stop retrying refused credentials, and make synchronous Dispose nonblocking. Add concurrency, writer-interleaving and disposal regressions.


First release. Targets **.NET 11** (`net11.0`) and **CodeLogic 5.0**; built on MQTTnet 5.2.

### Added

- **`MqttLibrary`** (`CL.Mqtt`): a CodeLogic `ILibrary` with `config.mqtt.json`.
  - **Connections:** named client connections (`Client()`, `Client(name)`, `Clients`), plus `CreateClient` for clients outside the configuration.
  - **Broker:** an optional embedded broker (`Broker`, `HasBroker`).
  - **Health:** the check reports the broker and every auto-connect connection.
  - **Live config changes:**
    - connections are added, removed or reconfigured in place, keeping their subscriptions;
    - broker user changes apply at once, and listener changes restart the broker.
- **`MqttClientService`:**
  - **Connection:** MQTT 3.1.1 and 5.0, TLS (optionally accepting untrusted certificates), credentials, last will, clean or persistent sessions.
  - **Reconnect:** automatic, with exponential back-off and jitter; state is reported as `MqttConnectionState`.
  - **Subscriptions:**
    - they are kept by the client and sent again on every connect, and can be registered before the first connect;
    - several handlers can share a filter, and disposing one does not affect the others.
  - **Publishing:** raw bytes, UTF-8 text and JSON (reflection or source-generated `JsonTypeInfo<T>`), with `MqttPublishOptions` for QoS, retain and MQTT 5 properties.
  - **Live settings:** `ReconfigureAsync` applies new settings and keeps subscriptions.
- **`MqttBrokerService`:**
  - **Listening:** binds to exactly the configured address (`127.0.0.1` by default; no stray IPv6 listener). An optional TLS
    listener (`broker.tls`: PKCS#12 certificate, port 8883, `disablePlainPort`) for brokers opened to a network; a broker
    reachable from the network without TLS logs a warning at start.
  - **Access:** `IMqttBrokerAuthorizer` decides connections, publishes and subscriptions. The default `ConfigBrokerAuthorizer`
    uses the configured users, compares passwords in constant time (clear text or a `MqttBrokerUser.HashPassword` PBKDF2-SHA256
    hash), checks topic permissions with filter coverage, honours `canPublish` / `canSubscribe` and the optional `clientIds` a
    user may connect with.
  - **Session protection:** client ids starting with `$` are refused (`$broker` is the broker's own); a client id belongs to
    the user that holds its session, so another user can neither take the session over (and inherit its subscriptions and
    queued messages) nor disconnect the client; `maxSessionsPerUser` (100) caps connected plus persistent sessions per user;
    `persistentSessionExpiryMinutes` (1 day) deletes persistent sessions nobody reconnects to. Refusals carry a `Reason` on
    `MqttBrokerConnectionRejectedEvent`.
  - **Taps and publishing:** `Tap` observes client publishes, and `PublishAsync` publishes as the broker. The broker's own
    messages are recognised by their session, never by the client id. `GetClientsAsync` lists connected clients.
  - **Retained messages:** persisted to `retained.json` in the library's data directory (batched writes by a single writer,
    atomic replace); `maxRetainedMessages` (10 000) and `maxRetainedPayloadBytes` (1 MiB) bound what clients can store
    (MQTT 5 publishers get "quota exceeded"; a clear always passes).
  - **Queues:** `maxPendingMessagesPerClient` drops the oldest queued message, as documented (the overflow strategy is set
    explicitly); clients have 15 s to send CONNECT.
- **Messages:** `MqttMessage` carries a payload copy, text/JSON readers and MQTT 5 properties.
- **Topics:** `MqttTopic` validates topics and filters, matches topics against filters, and checks filter coverage (`Covers`).
- **Events** on the CodeLogic bus: `MqttConnectedEvent`, `MqttDisconnectedEvent`, `MqttBrokerStartedEvent`,
  `MqttBrokerStoppedEvent`, `MqttBrokerClientConnectedEvent`, `MqttBrokerClientDisconnectedEvent`,
  `MqttBrokerConnectionRejectedEvent`.
- **Errors:** stable codes in `MqttErrorCodes` (`mqtt.not_connected`, `mqtt.authentication_failed`,
  `mqtt.subscribe_rejected`, ...).

### Security

- A client could connect with the client id `$broker` and publish anywhere unchecked and unseen by taps, because the
  broker's own injected messages were recognised by that id. Reserved ids are now refused and injected messages are
  recognised by their session items.
- Any authenticated client could resume another client's persistent session (inheriting its subscriptions and queued
  messages) or kick it off by reusing its client id. Client ids are now bound to the user holding them.
- Persistent sessions and retained messages had no bound; see the caps above.
- `cleanSession: false` did nothing on MQTT 5 because no session expiry was sent; `sessionExpirySeconds` (1 day) is now
  sent on `V500`.
- Refused credentials were retried like a network outage; they now stop automatic retries until an explicit ConnectAsync call.
  MQTTnet 5.2 exposes failure codes through exception text; the parser matches known enum names.
- The retained-message store could run two writers on the same temporary file and lose a snapshot; it has one writer.
- `publishTopics` / `subscribeTopics` set to `null` in JSON no longer throw; user names cannot contain whitespace or start
  with `$`; client ids in the connection config cannot start with `$`.
- `MqttLibrary` implements `IAsyncDisposable`; `Dispose()` no longer blocks on network I/O after a normal stop.

### Documentation

- The docs list `empty client id` as a broker refusal reason, and say that a `maxPendingMessagesPerClient` change
  restarts the broker. The README's trailing relative link, which broke on nuget.org, is gone.
