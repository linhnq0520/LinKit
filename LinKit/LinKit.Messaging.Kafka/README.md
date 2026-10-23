# LinKit.Messaging.Kafka

[![NuGet Version](https://img.shields.io/nuget/v/LinKit.Messaging.Kafka.svg)](https://www.nuget.org/packages/LinKit.Messaging.Kafka/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/LinKit.Messaging.Kafka.svg)](https://www.nuget.org/packages/LinKit.Messaging.Kafka/)

This package provides the Apache Kafka broker adapter for the messaging abstractions in `LinKit.Core`. Core source generation wires supported message declarations and consumers to this broker adapter.

## Prerequisites

*   You must have `LinKit.Core` installed.
*   You need access to a running Kafka cluster.
*   This package depends on the native `librdkafka` library via `Confluent.Kafka`. Ensure it is compatible with your target deployment environment, especially for AOT scenarios.

The `Confluent.Kafka` dependency uses native `librdkafka` components. Validate the package/runtime assets for each deployment OS and architecture. Source generation in LinKit does not itself certify the native Kafka client as Native AOT compatible.

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
`GroupId` is required for consumer groups. `MaxProcessingAttempts` controls handler retries before publishing a failed record to `<source-topic>.dlq`; `0` retries indefinitely and can block progress on that partition. The destination topic must be provisioned/configured according to your Kafka cluster policy.

LinKit commits Kafka offsets only after the generated message handler completes successfully. Failed handler calls are sought back to the failed record and retried with a delay; this is at-least-once delivery, so handlers should be idempotent. `EnableAutoCommit` is retained for configuration compatibility, but LinKit disables broker auto-commit to avoid acknowledging unprocessed records.

Kafka uses `MessageHeaderNames.PartitionKey` as the record key when present; otherwise it uses the producer's `routingKey`. The Outbox publisher supplies the Outbox message ID by default to avoid putting every message for a topic on one partition. Set the partition-key header to a stable aggregate key when per-aggregate ordering is required.

For trimmed or Native AOT applications, define and register a `System.Text.Json` source-generated context containing every message type used by producers and consumers:

```csharp
using System.Text.Json.Serialization;
using LinKit.Core.Messaging;

[JsonSerializable(typeof(OrderCreated))]
internal partial class AppMessageJsonContext : JsonSerializerContext;

LinKitMessageJson.AddContext(AppMessageJsonContext.Default);
```

Register the context before publishing or starting consumers. A reflection fallback may be available when dynamic code is enabled; do not rely on it in Native AOT.

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
`AddLinKitKafka` registers the `IBrokerProducer` and `IBrokerConnection` implementations. Offset commits occur after successful handler completion; failures seek back and retry. Delivery is at-least-once, so consumers should be idempotent. Publishing to a DLQ and committing the source offset are not one atomic transaction; a crash can duplicate a DLQ record.