# LinKit

LinKit is a modular .NET toolkit that uses Roslyn source generators to reduce repetitive CQRS, endpoint, mapping, data-access, and integration code. Install only the packages your application needs.

> Source generation is a design approach, not a blanket guarantee of zero reflection, exactly-once messaging, or Native AOT compatibility. See each package guide and the [architecture guide](./ARCHITECTURE_AND_CODEGEN_GUIDE.md) for concrete behavior and limitations.

## Supported targets

Runtime libraries target .NET 8, .NET 9, and .NET 10 unless a package/project states otherwise. The Roslyn generators target `netstandard2.0`. The Native AOT smoke application currently targets .NET 10; it does not certify every package, provider, operating system, or application composition.

## Choose packages

| Package | Purpose | Guide |
|---|---|---|
| `LinKit.Core` | CQRS contracts/mediator, generated DI/endpoints/jobs/mapping, messaging contracts. | [Core README](./LinKit.Core/README.md) |
| `LinKit.Data` | ADO.NET repositories, schema synchronization, migrations, LINQ/fluent query APIs. | [Data README](./LinKit.Data/README.md) |
| `LinKit.Grpc` | Generated gRPC CQRS server/client adapters. | [gRPC README](./LinKit.Grpc/README.md) |
| `LinKit.Json` | Fast, allocation-conscious JSON path navigation and typed value accessors. | [Json README](./LinKit.Json/README.md) |
| `LinKit.Messaging.Kafka` | Kafka broker integration for LinKit messaging. | [Kafka README](./LinKit.Messaging.Kafka/README.md) |
| `LinKit.Messaging.RabbitMQ` | RabbitMQ broker integration for LinKit messaging. | [RabbitMQ README](./LinKit.Messaging.RabbitMQ/README.md) |
| `LinKit.Outbox` | Outbox/Inbox contracts, in-memory stores, publisher and retry options. | [Outbox README](./LinKit.Outbox/README.md) |
| `LinKit.Outbox.Data` | Durable provider-neutral ADO.NET stores. | [Outbox.Data README](./LinKit.Outbox.Data/README.md) |
| `LinKit.Caching` | In-memory and Native AOT hybrid Redis cache provider. | [Caching README](./LinKit.Caching/README.md) |
| `LinKit.Resilience` | Explicit retry, timeout, and circuit-breaker pipeline. | [Resilience README](./LinKit.Resilience/README.md) |
| `LinKit.Tenancy` | Async-flow tenant context and explicit message-header helpers. | [Tenancy README](./LinKit.Tenancy/README.md) |
| `LinKit.Sagas` | Saga step coordination and repository abstraction. | [Sagas README](./LinKit.Sagas/README.md) |

Generators for Core, Data, and gRPC are distributed inside their corresponding runtime NuGet packages. Do not add the generator projects as runtime dependencies.

## Install and register CQRS

```shell
dotnet add package LinKit.Core
```

Define a request and handler:

```csharp
using LinKit.Core.Cqrs;

public sealed record GetGreetingQuery(string Name) : IQuery<string>;

[CqrsHandler]
public sealed class GetGreetingHandler : IQueryHandler<GetGreetingQuery, string>
{
    public Task<string> HandleAsync(
        GetGreetingQuery query,
        CancellationToken cancellationToken) =>
        Task.FromResult($"Hello, {query.Name}!");
}
```

Register generated services in the host and inject the mediator:

```csharp
using LinKit.Core;
using LinKit.Core.Cqrs;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddLinKitCqrs();
var app = builder.Build();

app.MapGet("/greeting/{name}", async (string name, IMediator mediator, CancellationToken ct) =>
    await mediator.QueryAsync(new GetGreetingQuery(name), ct));

app.Run();
```

## Package versions

Packages follow independent semantic versions. Current project versions are:

| Package | Version | Package | Version |
|---|---:|---|---:|
| `LinKit.Core` | 3.1.0 | `LinKit.Data` | 2.2.0 |
| `LinKit.Grpc` | 2.0.0 | `LinKit.Json` | 1.0.0 |
| `LinKit.Messaging.Kafka` | 1.2.0 | `LinKit.Messaging.RabbitMQ` | 1.3.0 |
| `LinKit.Outbox` | 1.1.0 | `LinKit.Outbox.Data` | 1.1.0 |
| `LinKit.Caching` | 1.2.0 | `LinKit.Resilience` | 1.1.0 |
| `LinKit.Tenancy` | 1.1.0 | `LinKit.Sagas` | 1.1.0 |

These are repository project metadata values. Core 3.0.0, Data 2.0.0, and gRPC 2.0.0 include generated namespace changes; see each package README for migration notes. Other package versions are unchanged.

## Build and test from source

From this directory:

```powershell
dotnet restore LinKit.sln
dotnet build LinKit.sln -c Release --no-restore
dotnet test Test\LinKit.Messaging.Tests\LinKit.Messaging.Tests.csproj -c Release
```

SQLite integration coverage runs in the regular test suite. Additional ADO.NET provider integration tests run when their connection-string environment variables are set; see [Outbox.Data testing instructions](./LinKit.Outbox.Data/README.md).

Publish/run the .NET 10 AOT smoke app for the target RID:

```powershell
dotnet publish Test\LinKit.AotSmoke\LinKit.AotSmoke.csproj -c Release -r win-x64 -warnaserror:IL2026,IL2060,IL2091,IL3050
.\Test\LinKit.AotSmoke\bin\Release\net10.0\win-x64\publish\LinKit.AotSmoke.exe
```

## Native AOT and delivery guarantees

- Register `System.Text.Json` generated contexts for messages and JSON columns.
- Review trimming/AOT warnings in the complete consuming application.
- Publish and run the actual RID; a successful library build is not an AOT test.
- Kafka and RabbitMQ delivery can be repeated; use idempotent handlers.
- Durable Outbox atomicity requires the same database connection/transaction as the business write.
- Saga and cache in-memory implementations are volatile; use a durable/distributed implementation where required.

For module behavior, startup sequences, database semantics, source-generation boundaries, testing, and known gaps, read the [architecture and code-generation guide](./ARCHITECTURE_AND_CODEGEN_GUIDE.md).

## License

MIT, as declared in the NuGet package metadata. See the [MIT license text](https://opensource.org/license/mit/).
