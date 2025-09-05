# LinKit.Messaging.Kafka

[![NuGet Version](https://img.shields.io/nuget/v/LinKit.Messaging.Kafka.svg)](https://www.nuget.org/packages/LinKit.Messaging.Kafka/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/LinKit.Messaging.Kafka.svg)](https://www.nuget.org/packages/LinKit.Messaging.Kafka/)

This package provides the **Apache Kafka implementation** for the abstractions defined in the `LinKit.Core` Messaging Kit. It allows the LinKit source generator to create publishers and consumers that communicate with a Kafka cluster.

## Prerequisites

*   You must have `LinKit.Core` installed.
*   You need access to a running Kafka cluster.
*   This package depends on the native `librdkafka` library via `Confluent.Kafka`. Ensure it is compatible with your target deployment environment, especially for AOT scenarios.

**For a complete guide on how to define messages with `[Message]` and create handlers with `[CqrsHandler]`, please refer to the [main LinKit documentation](https://github.com/your-username/LinKit).**

## Installation

```shell
dotnet add package LinKit.Messaging.Kafka
```

## How to Use

### 1. Configure `appsettings.json`

Add a configuration section for your Kafka connection. The default section name is `"Kafka"`.

```json
{
  "Kafka": {
    "BootstrapServers": "localhost:9092",
    "GroupId": "my-application-consumer-group"
  }
}
```
*The `GroupId` is essential for your consumers.*

### 2. Register Services in `Program.cs`

In your application's startup code (e.g., `Program.cs` for a Worker Service), use the `AddLinKitKafka()` extension method **after** registering the core LinKit services.

```csharp
using LinKit.Core;
using LinKit.Messaging.Kafka;

var builder = Host.CreateApplicationBuilder(args);

// 1. Register core LinKit services. 
// This generates the IMessagePublisher and your consumer IHostedService(s).
builder.Services.AddLinKitCqrs();
builder.Services.AddLinKitMessaging(); 

// 2. Provide the Kafka implementation.
// This "plugs in" Kafka to the generated services.
builder.Services.AddLinKitKafka(builder.Configuration);

var host = builder.Build();
host.Run();
```
That's it! Your application is now configured to publish and consume messages via Kafka. The `AddLinKitKafka` method registers the necessary `IBrokerProducer` and `IBrokerConnection` implementations, allowing your auto-generated services to connect to Kafka seamlessly.
```