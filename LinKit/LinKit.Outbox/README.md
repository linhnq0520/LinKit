# LinKit.Outbox

[![NuGet](https://img.shields.io/nuget/v/LinKit.Outbox.svg)](https://www.nuget.org/packages/LinKit.Outbox/)

`LinKit.Outbox` provides Outbox/Inbox contracts, an in-memory implementation, configurable retry/backoff options, and a hosted publisher that sends stored records through LinKit broker abstractions.

> The default in-memory stores are for development and tests only. They lose data when the process exits. For durable ADO.NET persistence, use [`LinKit.Outbox.Data`](https://www.nuget.org/packages/LinKit.Outbox.Data/).

## Install

```shell
dotnet add package LinKit.Outbox
```

Add a broker adapter (for example `LinKit.Messaging.Kafka` or `LinKit.Messaging.RabbitMQ`) when the publisher should send to that broker. Add a durable store adapter for production.

## Register services

For local development with in-memory stores:

```csharp
builder.Services.AddLinKitOutbox(options =>
{
    options.PollInterval = TimeSpan.FromSeconds(2);
    options.BatchSize = 100;
    options.MaxRetries = 5;
    options.LeaseDuration = TimeSpan.FromSeconds(30);
    options.BaseRetryDelay = TimeSpan.FromSeconds(2);
    options.MaxRetryDelay = TimeSpan.FromMinutes(5);
});
```

For durable ADO.NET storage, configure `LinKit.Data` and then call `AddLinKitAdoNetOutbox` from `LinKit.Outbox.Data`. Do not register both in-memory and durable extensions unless you intentionally replace the store registrations.

## Record an Outbox message

Persist the business state and the message in the same transaction. The application must use the same connection and transaction for both writes:

```csharp
await unitOfWork.BeginTransactionAsync(cancellationToken);
try
{
    await orders.InsertAsync(order, cancellationToken);
    await outbox.SaveMessageAsync(new OutboxMessage
    {
        MessageType = nameof(OrderCreated),
        Payload = JsonSerializer.Serialize(
            new OrderCreated(order.Id),
            AppJsonContext.Default.OrderCreated),
        Headers = new MessageHeaders
        {
            [MessageHeaderNames.CorrelationId] = correlationId
        }
    }, cancellationToken);

    await unitOfWork.CommitAsync(cancellationToken);
}
catch
{
    await unitOfWork.RollbackAsync(CancellationToken.None);
    throw;
}
```

If there is no ambient shared transaction, a store may make its own Outbox rows atomic, but that does not make them atomic with a business database write performed elsewhere.

`OutboxMessage.Create<TMessage>(message, payload, headers)` is a helper for constructing records. The payload is persisted as a JSON string and must be valid JSON when the publisher parses it before broker publication.

For Kafka, the publisher uses each Outbox message ID as the default partition key so that a topic's messages are not all pinned to one partition. To preserve ordering for a business aggregate, set `MessageHeaderNames.PartitionKey` in `Headers` to that aggregate's stable key before saving the Outbox message. The header is carried to the broker adapter; non-Kafka adapters can ignore it.

## Consume with Inbox deduplication

Use a stable consumer name and message ID:

```csharp
if (await inbox.HasBeenProcessedAsync(messageId, nameof(OrderConsumer), cancellationToken))
{
    return;
}

await ApplyOrderChangeAsync(message, cancellationToken);
await inbox.MarkProcessedAsync(
    messageId,
    nameof(OrderConsumer),
    expiry: TimeSpan.FromDays(7),
    cancellationToken);
```

For durable correctness, commit the side effect and Inbox mark in the same transaction. `HasBeenProcessedAsync` followed by `MarkProcessedAsync` is not an atomic claim: two concurrent deliveries can both pass the check. Use an atomic reservation/locking strategy if concurrent duplicate execution must be prevented.

## Delivery semantics and retries

- Publisher claims a batch, publishes each record, then marks it processed.
- A durable store can implement a lease so concurrent publisher instances usually skip active claims. The default `IOutboxStore` claim method falls back to querying pending rows; it is not a distributed lock.
- Delivery is **at least once**. If the broker accepted a message but the process failed before the processed mark committed, it can be published again.
- Retry delay uses bounded exponential backoff. `MaxRetries` determines when a row is no longer selected.
- Exhausted rows are logged/left in the store as exhausted records. The publisher does not move them into a broker dead-letter exchange/topic.
- Consumers and side effects must be idempotent.

## Configure message JSON for trimming/AOT

Use a source-generated `System.Text.Json` context for message types and register it before publishing or starting consumers:

```csharp
using System.Text.Json.Serialization;
using LinKit.Core.Messaging;

[JsonSerializable(typeof(OrderCreated))]
internal partial class AppMessageJsonContext : JsonSerializerContext;

LinKitMessageJson.AddContext(AppMessageJsonContext.Default);
```

The runtime may have a reflection serializer fallback in non-AOT execution. A generated context is the recommended path for trimmed/Native AOT applications; publish and run the exact application configuration to verify it.

## Public interfaces

- `IOutboxStore`: save, query, claim, mark processed, and schedule failed records.
- `IInboxStore`: consumer-scoped processed checks and optional expiry.
- `OutboxOptions`: publisher timing, batch/lease sizes, retry count and backoff limits.
- `OutboxPublisherService`: hosted polling/publish loop registered by `AddLinKitOutbox`.

See [`LinKit.Outbox.Data`](https://www.nuget.org/packages/LinKit.Outbox.Data/) for the provider-neutral ADO.NET stores and provider setup.

## License

MIT. See the [repository license](https://github.com/linhnq0520/LinKit/blob/main/LICENSE).
