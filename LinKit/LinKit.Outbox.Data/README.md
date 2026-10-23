# LinKit.Outbox.Data

[![NuGet](https://img.shields.io/nuget/v/LinKit.Outbox.Data.svg)](https://www.nuget.org/packages/LinKit.Outbox.Data/)

`LinKit.Outbox.Data` adds durable ADO.NET implementations of `IOutboxStore` and `IInboxStore`. It reuses the connection factory and unit of work from `LinKit.Data` and includes SQL dialect support for SQLite, PostgreSQL, SQL Server, MySQL, and Oracle.

Delivery is **at least once**. The adapter does not claim exactly-once delivery or make a business database transaction atomic with a broker publish.

## Requirements

- .NET 8, .NET 9, or .NET 10.
- `LinKit.Outbox`, `LinKit.Data`, and the ADO.NET driver for your database.
- A database/schema that the configured connection can reach.

Install the LinKit packages and one provider:

```shell
dotnet add package LinKit.Data
dotnet add package LinKit.Outbox
dotnet add package LinKit.Outbox.Data

# Install exactly one provider package used by your application:
dotnet add package Npgsql
# or Microsoft.Data.SqlClient, MySqlConnector,
# Oracle.ManagedDataAccess.Core, or Microsoft.Data.Sqlite
```

## Configure dependency injection

Register `LinKit.Data` first, then the durable adapter:

```csharp
using LinKit.Data;
using LinKit.Outbox.Data;
using Npgsql;

var builder = Host.CreateApplicationBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("AppDatabase")
    ?? throw new InvalidOperationException("Connection string 'AppDatabase' is required.");

builder.Services.AddLinKitData(options =>
{
    options.UseConnection(
        DatabaseDialect.PostgreSql,
        () => new NpgsqlConnection(connectionString));

    // Data schema synchronization and migrations are configured separately.
    options.AutoSyncSchema = true;
});

builder.Services.AddLinKitAdoNetOutbox(options =>
{
    options.BatchSize = 100;
    options.PollInterval = TimeSpan.FromSeconds(2);
    options.LeaseDuration = TimeSpan.FromSeconds(30);
    options.MaxRetries = 5;
});

await builder.Build().RunAsync();
```

Select the dialect matching the provider connection:

| Provider | Dialect | Example connection |
|---|---|---|
| Microsoft.Data.Sqlite | `DatabaseDialect.Sqlite` | `new SqliteConnection(connectionString)` |
| Npgsql | `DatabaseDialect.PostgreSql` | `new NpgsqlConnection(connectionString)` |
| Microsoft.Data.SqlClient | `DatabaseDialect.SqlServer` | `new SqlConnection(connectionString)` |
| MySqlConnector | `DatabaseDialect.MySql` | `new MySqlConnection(connectionString)` |
| Oracle.ManagedDataAccess.Core | `DatabaseDialect.Oracle` | `new OracleConnection(connectionString)` |

At host startup, `AddLinKitAdoNetOutbox` creates the Outbox/Inbox tables and the Outbox pending index before the publisher hosted service runs. The database principal needs DDL permission for these objects. If production principals cannot create schema, provision the tables/index through deployment tooling before starting the host.

## Write business data and an Outbox record atomically

Use the same scoped `IUnitOfWork` for the application data and Outbox store. The Outbox command enlists in `CurrentTransaction` when one is active:

```csharp
public sealed class PlaceOrderHandler(
    IUnitOfWork unitOfWork,
    IRepository<Order, Guid> orders,
    IOutboxStore outbox)
{
    public async Task<Guid> HandleAsync(PlaceOrder command, CancellationToken cancellationToken)
    {
        var order = new Order(command.OrderId, command.CustomerId);

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
                    [MessageHeaderNames.CorrelationId] = command.CorrelationId
                }
            }, cancellationToken);

            await unitOfWork.CommitAsync(cancellationToken);
            return order.Id;
        }
        catch
        {
            await unitOfWork.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
```

The exact order and transaction lifetime must match the transaction semantics of the business repository. This guarantee only holds when both operations use the same underlying connection/transaction. If no ambient transaction exists, `SaveAsync` starts a transaction for the Outbox rows only; it cannot atomically include writes made through a different ORM connection or database.

## Inbox deduplication

The store keys records by both message ID and consumer name and can set an expiry:

```csharp
if (await inbox.HasBeenProcessedAsync(messageId, nameof(OrderConsumer), cancellationToken))
{
    return;
}

await ApplyBusinessChangesAsync(message, cancellationToken);
await inbox.MarkProcessedAsync(
    messageId,
    nameof(OrderConsumer),
    expiry: TimeSpan.FromDays(7),
    cancellationToken);
```

For durable deduplication, perform the side effect and `MarkProcessedAsync` in the same transaction. The API is a check-then-mark contract, not an atomic reservation protocol: parallel consumers can both pass `HasBeenProcessedAsync` before either records completion. If concurrent duplicate execution must be excluded, use transactional locking/unique constraints around the full business operation or implement an atomic inbox claim protocol.

## Publisher behavior and operations

- Pending records are claimed with a lease; expired leases become eligible again.
- Failed publishes increment retry count and can be delayed with exponential backoff.
- Exhausted rows stop being returned as pending. They are not automatically moved to a broker DLQ or archive table.
- A process can publish successfully and crash before marking the row processed; the record can be published again.
- Consumers and downstream side effects should therefore be idempotent.
- Monitor pending age/count, retry exhaustion, publisher errors, and lease expiry in application observability.

## Provider verification

The test project contains integration scenarios for SQLite and optional live providers. Provider scenarios run only when the corresponding connection-string environment variable is set:

```text
LINKIT_TEST_POSTGRES
LINKIT_TEST_SQLSERVER
LINKIT_TEST_MYSQL
LINKIT_TEST_ORACLE
```

Run all configured provider scenarios:

```shell
dotnet test Test/LinKit.Messaging.Tests/LinKit.Messaging.Tests.csproj -c Release --filter "FullyQualifiedName~DatabaseProviderIntegrationTests"
```

The scenarios verify schema idempotence, rollback, save/read, lease claims, retry scheduling, completion, Inbox upsert, and expiry. Before production use, test the exact driver/server versions and concurrency profile used by your application; DDL permissions and SQL semantics can vary by provider configuration.

## Native AOT and serialization

Outbox header storage uses a small built-in JSON codec. Broker payload serialization is provided by the broker/application path and is separate from this package. For trimmed or Native AOT broker serialization, register a `System.Text.Json` generated context with `LinKitMessageJson.AddContext(...)` and test the published application.

The ADO.NET adapter itself has passed the repository AOT smoke scenario with SQLite on .NET 10. That smoke does not certify every provider driver or every application composition.

## License

MIT. See the [repository license](https://github.com/linhnq0520/LinKit/blob/main/LICENSE).
