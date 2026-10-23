# LinKit.Grpc

[![NuGet](https://img.shields.io/nuget/v/LinKit.Grpc.svg)](https://www.nuget.org/packages/LinKit.Grpc/)

`LinKit.Grpc` packages a Roslyn generator and runtime adapters that connect CQRS request types to protobuf-generated gRPC service/client types. You still define and compile `.proto` contracts with the gRPC tooling; LinKit generates adapters and property mapping for supported shapes.

## Install

```shell
dotnet add package LinKit.Core
dotnet add package LinKit.Grpc
dotnet add package Grpc.AspNetCore
```

The application also needs generated C# code for its protobuf contracts, usually produced by `Grpc.Tools` in the contract/server project. Add the required gRPC tooling package to that project.

## Expose a CQRS query as a server RPC

Given a protobuf service:

```protobuf
syntax = "proto3";
option csharp_namespace = "MyApp.Contracts";

service InventoryService {
  rpc CheckStock (CheckStockRequest) returns (CheckStockResponse);
}

message CheckStockRequest {
  string product_code = 1;
  int32 quantity = 2;
}

message CheckStockResponse {
  bool available = 1;
  int32 remaining = 2;
}
```

Declare the CQRS request and handler:

```csharp
using LinKit.Core.Cqrs;
using LinKit.Grpc;
using MyApp.Contracts;

[GrpcEndpoint(typeof(InventoryService.InventoryServiceBase), "CheckStock")]
public sealed class CheckStockQuery : IQuery<CheckStockResponse>
{
    public string ProductCode { get; set; } = string.Empty;
    public int Quantity { get; set; }
}

[CqrsHandler]
public sealed class CheckStockHandler : IQueryHandler<CheckStockQuery, CheckStockResponse>
{
    public Task<CheckStockResponse> HandleAsync(
        CheckStockQuery query,
        CancellationToken cancellationToken) =>
        Task.FromResult(new CheckStockResponse
        {
            Available = query.Quantity <= 50,
            Remaining = Math.Max(0, 50 - query.Quantity)
        });
}
```

Register the gRPC server and map generated services:

```csharp
builder.Services.AddGrpc();
builder.Services.AddLinKitCqrs();
builder.Services.AddLinKitGrpcServer();

var app = builder.Build();
app.MapLinKitGrpcServices();
app.Run();
```

The generated service adapter maps the protobuf request into the CQRS request, invokes the mediator, and maps the handler response back to the protobuf response. Validate property names/types and generated source for nested or custom mappings; the generator supports defined property/list mapping patterns, not arbitrary object mapping.

## Call a remote RPC through `IGrpcMediator`

In a client compilation, annotate the CQRS request with the generated protobuf client and RPC method name:

```csharp
using LinKit.Core.Cqrs;
using LinKit.Grpc;
using MyApp.Contracts;

[GrpcClient(typeof(InventoryService.InventoryServiceClient), "CheckStockAsync")]
public sealed class CheckStockRemoteQuery : IQuery<CheckStockResponse>
{
    public string ProductCode { get; set; } = string.Empty;
    public int Quantity { get; set; }
}
```

Register the generated mediator and a channel provider. `AddLinKitGrpcClient()` supplies defaults for interceptor/metadata providers and the client factory; an `IGrpcChannelProvider` is required:

```csharp
builder.Services.AddLinKitCqrs();
builder.Services.AddLinKitGrpcClient();
builder.Services.AddGrpcChannel("https://inventory.example.net");
```

`AddGrpcChannel` registers `DefaultGrpcChannelProvider`, which reuses a channel for each generated gRPC client type. For multiple service addresses, implement the provider:

```csharp
using System.Collections.Concurrent;
using Grpc.Core;
using Grpc.Net.Client;
using LinKit.Grpc;

public sealed class ServiceChannelProvider(IConfiguration configuration) :
    IGrpcChannelProvider, IDisposable
{
    private readonly ConcurrentDictionary<Type, GrpcChannel> _channels = new();

    public GrpcChannel GetChannelFor<TClient>() where TClient : ClientBase =>
        _channels.GetOrAdd(typeof(TClient), clientType =>
        {
            var address = configuration[$"GrpcClients:{clientType.Name}"]
                ?? throw new InvalidOperationException(
                    $"No gRPC address configured for {clientType.Name}.");
            return GrpcChannel.ForAddress(address);
        });

    public void Dispose()
    {
        foreach (var channel in _channels.Values)
        {
            channel.Dispose();
        }
    }
}
```

```csharp
builder.Services.AddGrpcChannelProvider<ServiceChannelProvider>();
```

Then use the generated remote mediator:

```csharp
var response = await grpcMediator.QueryAsync(
    new CheckStockRemoteQuery { ProductCode = "SKU-42", Quantity = 3 },
    cancellationToken);
```

## Interceptors and metadata

Providers are per client type and use the actual public interfaces:

```csharp
public sealed class AppInterceptorProvider : IGrpcInterceptorProvider
{
    public Interceptor[] GetInterceptorsFor<TClient>() => [];
}

public sealed class AppMetadataProvider : IMetadataProvider
{
    public Metadata? GetMetadata() => new Metadata();
}
```

Register them with the service collection helpers:

```csharp
builder.Services.AddGrpcInterceptorProvider<AppInterceptorProvider>();
builder.Services.AddGrpcMetadataProvider<AppMetadataProvider>();
```

Metadata is requested for each outgoing RPC. Do not put untrusted or secret values into logs. Configure authentication/TLS and deadlines using application-specific gRPC options/interceptors.

## Runtime and AOT considerations

- Reuse channels and dispose the provider with the host; do not create a channel per request.
- Keep client/server protobuf contracts compatible across deployments.
- Client factory activation uses generated activators when available; a reflection-based activation fallback may remain for unregistered client types.
- This package's source generation does not certify AOT safety for every gRPC runtime, protobuf type, interceptor, or application configuration. Enable trim/AOT analyzers and publish/run the target application for its RID.

## License

MIT. See the [repository license](https://github.com/linhnq0520/LinKit/blob/main/LICENSE).
