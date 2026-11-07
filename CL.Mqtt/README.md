# CodeLogic.Mqtt

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.Mqtt)](https://www.nuget.org/packages/CodeLogic.Mqtt)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](https://github.com/zyntal-com/CodeLogic.Libs/blob/main/LICENSE)

> MQTT for [CodeLogic 5](https://github.com/zyntal-com/CodeLogic): named client connections with automatic reconnect, and an optional embedded MQTT broker.

- **Clients:** connect to any MQTT 3.1.1 or 5.0 broker, with TLS, credentials and a last will.
  - **Reconnect:** a lost connection is retried with exponential back-off.
  - **Subscriptions:** they belong to the client, so they survive reconnects and can be registered before the first connect.
  - **Publishing:** text, bytes or JSON.
- **Embedded broker:** run an MQTT server inside your app.
  - **Access:** user accounts (clear or hashed passwords) with per-user topic permissions and optional pinned client ids; it binds to `127.0.0.1` by default, and has a TLS listener for anything else.
  - **Protection:** a client id belongs to the user holding it (no session takeover), `$` ids are reserved, sessions per user and retained messages are capped, idle persistent sessions expire.
  - **Retained messages:** kept across restarts.
  - **Taps and publishing:** taps see what clients publish, and the broker can publish as itself.
- **CodeLogic conventions:**
  - **Errors:** every fallible call returns `Result` / `Result<T>` with a stable `mqtt.*` error code (`MqttErrorCodes`).
  - **Config and events:** settings live in `config.mqtt.json` and apply live; connection events go to the CodeLogic event bus.
  - **Health:** the health check reports the broker and every connection.

Built on [MQTTnet](https://github.com/dotnet/MQTTnet) 5. MQTTnet types do not appear in the public API, except the documented `RawClient` / `RawServer` escape hatches.

## Install

```bash
dotnet add package CodeLogic.Mqtt
```

## Quick start

```csharp
using CL.Mqtt;
using CL.Mqtt.Models;

await Libraries.LoadAsync<MqttLibrary>();
await CodeLogic.ConfigureAsync();   // writes config.mqtt.json with a "Default" connection to localhost:1883
await CodeLogic.StartAsync();       // connects (and starts the broker when enabled)

var mqtt = Libraries.Get<MqttLibrary>();
var client = mqtt.Client().ValueOrThrow();

// Subscribe: wildcards work, the handler gets a copy of the message.
var subscription = (await client.SubscribeAsync("home/+/temperature", message =>
{
    Console.WriteLine($"{message.Topic}: {message.PayloadAsString()}");
})).ValueOrThrow();

// Publish text, bytes or JSON.
await client.PublishAsync("home/kitchen/temperature", "21.5");
await client.PublishJsonAsync("home/kitchen/state", new { on = true, level = 80 }, options: MqttPublishOptions.Retained);

await subscription.DisposeAsync();  // stop receiving
```

## Embedded broker

```json
{
  "enabled": true,
  "connections": { "Default": { "host": "127.0.0.1", "port": 1883 } },
  "broker": {
    "enabled": true,
    "port": 1883,
    "bindAddress": "0.0.0.0",
    "tls": { "enabled": true, "port": 8883, "certificatePath": "C:/certs/broker.pfx", "certificatePassword": "…", "disablePlainPort": true },
    "allowAnonymous": false,
    "users": [
      { "username": "zigbee2mqtt", "password": "…", "publishTopics": ["zigbee2mqtt/#"], "subscribeTopics": ["zigbee2mqtt/#"], "clientIds": ["zigbee2mqtt"] },
      { "username": "dashboard",   "password": "$pbkdf2-sha256$600000$…", "canPublish": false, "subscribeTopics": ["home/#"] }
    ]
  }
}
```

Empty `publishTopics` / `subscribeTopics` allow *every* topic; use `canPublish: false` / `canSubscribe: false` to
forbid one direction. `MqttBrokerUser.HashPassword("…")` makes the hashed form of a password. Open the broker to a
network only with `tls` on: without it, passwords and payloads travel in clear (the broker logs a warning).

```csharp
var broker = mqtt.Broker;

// See what clients publish, without a client of your own.
using var tap = broker.Tap("zigbee2mqtt/#", (message, ct) =>
{
    Console.WriteLine($"{message.SenderClientId} → {message.Topic}");
    return Task.CompletedTask;
}).ValueOrThrow();

// Publish as the broker itself.
await broker.PublishAsync("home/announcements", "Dinner is ready");

// Bring your own accounts: replace the authorizer between Initialize and Start.
broker.Authorizer = new MyAccountAuthorizer();
```

## Documentation

Full guide, configuration reference and error codes: [docs/libs/mqtt.md](https://github.com/zyntal-com/CodeLogic.Libs/blob/main/docs/libs/mqtt.md).

## License

MIT
