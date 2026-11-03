# CL.Mqtt — Changelog

All notable changes to **CodeLogic.Mqtt** are documented here. Versions follow
[Semantic Versioning](https://semver.org/).

## 5.0 (unreleased)

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
  - **Listening:** binds to exactly the configured address (`127.0.0.1` by default; no stray IPv6 listener).
  - **Access:** `IMqttBrokerAuthorizer` decides connections, publishes and subscriptions. The default `ConfigBrokerAuthorizer` uses the configured users, compares passwords in constant time and checks topic permissions with filter coverage.
  - **Taps and publishing:** `Tap` observes client publishes, and `PublishAsync` publishes as the broker. `GetClientsAsync` lists connected clients.
  - **Retained messages:** persisted to `retained.json` in the library's data directory (batched writes, atomic replace).
- **Messages:** `MqttMessage` carries a payload copy, text/JSON readers and MQTT 5 properties.
- **Topics:** `MqttTopic` validates topics and filters, matches topics against filters, and checks filter coverage (`Covers`).
- **Events** on the CodeLogic bus: `MqttConnectedEvent`, `MqttDisconnectedEvent`, `MqttBrokerStartedEvent`,
  `MqttBrokerStoppedEvent`, `MqttBrokerClientConnectedEvent`, `MqttBrokerClientDisconnectedEvent`,
  `MqttBrokerConnectionRejectedEvent`.
- **Errors:** stable codes in `MqttErrorCodes` (`mqtt.not_connected`, `mqtt.authentication_failed`,
  `mqtt.subscribe_rejected`, ...).
