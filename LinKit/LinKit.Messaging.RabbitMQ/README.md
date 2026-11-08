# LinKit.Messaging.RabbitMQ

This package provides a RabbitMQ broker adapter for the messaging abstractions in `LinKit.Core`. Core source generation wires supported message declarations and consumers to this broker adapter.

## Prerequisites

- You must have `LinKit.Core` installed.
- You need access to a running RabbitMQ instance.

Generated consumers remain active until host shutdown. Failed messages are retried by republishing with an incremented `x-linkit-attempt` header (bounded, immediate retries): `MaxRetryAttempts` (default 3, `0` = no retries) caps the attempts, and `RequeueOnFailure=false` skips retries entirely. After the cap, messages go to `DeadLetterExchange` if configured; otherwise they are nacked without requeue — a queue-level broker DLX applies, and if none exists the message is dropped with a warning. Retries are bounded, so a failing handler can no longer create a poison-message loop. Handlers should still be idempotent and deployments should define a dead-letter policy.

For trimmed or Native AOT applications, define and register a `System.Text.Json` source-generated context containing every message type used by producers and consumers:

```csharp
using System.Text.Json.Serialization;
using LinKit.Core.Messaging;

[JsonSerializable(typeof(OrderCreated))]
internal partial class AppMessageJsonContext : JsonSerializerContext;

LinKitMessageJson.AddContext(AppMessageJsonContext.Default);
```

Register the context before publishing or starting consumers. A reflection fallback may be available when dynamic code is enabled; do not rely on it in Native AOT.

## Installation

```shell
dotnet add package LinKit.Messaging.RabbitMQ
```

## How to Use

### 1. Configure `appsettings.json`

Add a configuration section for your RabbitMQ connection. The default section name is `"RabbitMQ"`.

```json
{
  "RabbitMQ": {
    "HostName": "localhost",
    "UserName": "guest",
    "Password": "guest",
    "Port": 5672,
    "VirtualHost": "/",
    "PrefetchCount": 50,
    "ChannelPoolSize": 64,
    "DefaultExchangeType": "topic",
    "PublisherConfirms": true,
    "RequeueOnFailure": true,
    "MaxRetryAttempts": 3,
    "DeadLetterExchange": "orders.dlx"
  }
}
```

`MaxRetryAttempts` defaults to 3 (`0` disables retries); `DeadLetterExchange` is optional.

#### Configuration reference

| Property              | Default     | Purpose                                                                                                                                                                                                                          |
| :-------------------- | :---------- | :------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `HostName`            | `localhost` | Broker host name or IP address.                                                                                                                                                                                                  |
| `UserName`            | `guest`     | User name used for the AMQP handshake.                                                                                                                                                                                           |
| `Password`            | `guest`     | Password for the AMQP handshake; load it from a secret store in production.                                                                                                                                                      |
| `Port`                | `5672`      | AMQP port: `5672` for plain TCP, `5671` for TLS.                                                                                                                                                                                 |
| `VirtualHost`         | `/`         | Virtual host to operate in.                                                                                                                                                                                                      |
| `PrefetchCount`       | `50`        | Unacknowledged deliveries allowed per consumer channel.                                                                                                                                                                          |
| `ChannelPoolSize`     | `64`        | Producer channel pool size; larger values trade broker sockets for throughput.                                                                                                                                                   |
| `DefaultExchangeType` | `topic`     | Exchange type declared when a message does not specify its own: `direct`, `topic`, `fanout` or `headers`.                                                                                                                        |
| `PublisherConfirms`   | `true`      | Wait for the broker acknowledgement after every publish. Disabling it raises throughput but allows silently dropped messages.                                                                                                    |
| `RequeueOnFailure`    | `true`      | Republish a failed delivery up to `MaxRetryAttempts` times. `false` sends the first failure straight to the dead-letter step.                                                                                                    |
| `MaxRetryAttempts`    | `3`         | Retry cap after a processing failure; `0` disables retries. Negative values are clamped to `0`.                                                                                                                                  |
| `DeadLetterExchange`  | unset       | Exchange that receives exhausted messages. Declared durable/direct when absent; existing exchanges are used as-is. When unset, exhausted messages are nacked without requeue, so a queue-level DLX applies if one is configured. |

### 2. Register Services in `Program.cs`

In your application's startup code (e.g., `Program.cs` for a Worker Service), use the `AddLinKitRabbitMQ()` extension method **after** registering the core LinKit services.

```csharp
using LinKit.Core;
using LinKit.Messaging.RabbitMQ;

var builder = Host.CreateApplicationBuilder(args);

// 1. Register core LinKit services.
// This generates the IMessagePublisher and your consumer IHostedService(s).
builder.Services.AddLinKitCqrs();
builder.Services.AddLinKitMessaging();

// 2. Provide the RabbitMQ implementation.
// This "plugs in" RabbitMQ to the generated services.
builder.Services.AddLinKitRabbitMQ(builder.Configuration);

var host = builder.Build();
host.Run();
```

`AddLinKitRabbitMQ` registers the producer and consumer connection. Producer sends persistent messages, supports configurable exchange types, header mapping (`CorrelationId`, `ReplyTo`, `MessageId`), and publisher confirms; consumer uses manual acknowledgements and supports dynamic exchange bindings and topologies. Delivery remains at-least-once. The adapter implements bounded immediate retries (`MaxRetryAttempts`, via the `x-linkit-attempt` header) and optional dead-letter routing (`DeadLetterExchange`); delayed retries are not implemented. Configure broker dead-letter routing appropriately to avoid poison-message loops. Test reconnection/recovery behavior against the RabbitMQ version and topology used in production.

## Configuration validation

`AddLinKitRabbitMQ` registers an `IValidateOptions<RabbitMqOptions>`, so configuration problems surface the first time `IOptions<RabbitMqOptions>.Value` is resolved (for the hosted consumer, during startup) as an `OptionsValidationException` instead of on the first publish. Validated: `HostName` and `UserName` must not be blank, `Port` must be between 1 and 65535, `VirtualHost` must not be null, `ChannelPoolSize` must be positive, and `DefaultExchangeType` must be `direct`, `topic`, `fanout` or `headers`.

## Consumer and producer reliability

- Consumer channel operations are serialized, making acknowledgements thread-safe; channel setup failures dispose the channel. Retries republish directly to the failing queue via the default exchange (`""`) while preserving `x-linkit-original-routing-key` so other queues bound to the same exchange do not receive duplicate messages.
- Producer confirms are event-driven and fully async — no `WaitForConfirmsOrDie` thread blocking. Any `ExchangeDeclare` failure, confirm timeout, or nack throws and safely disposes the channel instead of returning it to the pool.
- The exchange declaration cache is keyed by (name, type), so the same exchange name declared with different types is not conflated.

## License

MIT. See the [repository license](https://github.com/linhnq0520/LinKit/blob/main/LICENSE).

