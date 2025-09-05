# LinKit.Messaging.RabbitMQ

[![NuGet Version](https://img.shields.io/nuget/v/LinKit.Messaging.RabbitMQ.svg)](https://www.nuget.org/packages/LinKit.Messaging.RabbitMQ/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/LinKit.Messaging.RabbitMQ.svg)](https://www.nuget.org/packages/LinKit.Messaging.RabbitMQ/)

This package provides the **RabbitMQ implementation** for the abstractions defined in the `LinKit.Core` Messaging Kit. It allows the LinKit source generator to create publishers and consumers that communicate over a RabbitMQ message broker.

## Prerequisites

*   You must have `LinKit.Core` installed.
*   You need access to a running RabbitMQ instance.

**For a complete guide on how to define messages with `[Message]` and create handlers with `[CqrsHandler]`, please refer to the [main LinKit documentation](https://github.com/your-username/LinKit).**

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
    "Port": 5672
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

That's it! Your application is now configured to publish and consume messages via RabbitMQ. The `AddLinKitRabbitMQ` method registers the necessary `IBrokerProducer` and `IBrokerConnection` implementations, allowing your auto-generated services to connect to RabbitMQ seamlessly.
```
