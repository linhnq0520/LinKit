# LinKit.Messaging.RabbitMQ

[![NuGet Version](https://img.shields.io/nuget/v/LinKit.Messaging.RabbitMQ.svg)](https://www.nuget.org/packages/LinKit.Messaging.RabbitMQ/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/LinKit.Messaging.RabbitMQ.svg)](https://www.nuget.org/packages/LinKit.Messaging.RabbitMQ/)

This package provides a RabbitMQ broker adapter for the messaging abstractions in `LinKit.Core`. Core source generation wires supported message declarations and consumers to this broker adapter.

## Prerequisites

*   You must have `LinKit.Core` installed.
*   You need access to a running RabbitMQ instance.

Generated consumers remain active until host shutdown. Failed deliveries are requeued by default (`RequeueOnFailure=true`); configure a dead-letter exchange and set this option to `false` only when rejected messages are routed to a durable dead-letter queue. Requeued poison messages can otherwise redeliver repeatedly, so handlers should be idempotent and deployments should define a dead-letter policy.

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
    "RequeueOnFailure": true
  }
}
```

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

`AddLinKitRabbitMQ` registers the producer and consumer connection. Producer sends persistent messages and waits for publisher confirms; consumer uses manual acknowledgements. Delivery remains at-least-once. This adapter does not implement retry counters, delayed retries, or a DLQ policy. Configure broker dead-letter routing and set `RequeueOnFailure` appropriately to avoid poison-message loops. Test reconnection/recovery behavior against the RabbitMQ version and topology used in production.
