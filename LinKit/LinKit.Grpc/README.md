# LinKit.Grpc

**LinKit.Grpc** extends LinKit.Core to provide automatic gRPC server and client code generation based on your CQRS requests and handlers.

## Features

- **gRPC Server:**  
  - Generate gRPC service implementations from CQRS requests marked with `[GrpcEndpoint]`.
  - No manual mapping or boilerplate needed.
- **gRPC Client:**  
  - Generate mediator-based gRPC clients for remote CQRS requests.
  - Seamless integration with LinKit mediator pattern.
- **Automatic Mapping:**  
  - Properties are mapped by name or via `[JsonPropertyName]`/mapping configuration.
- **AOT & Trimming Ready:**  
  - Fully compatible with NativeAOT and trimming.

## Installation

```shell
dotnet add package LinKit.Grpc
```

## Quick Start

### 1. gRPC Server

**Mark your CQRS request:**
```csharp
[GrpcEndpoint(typeof(MyServiceBase), "GetUser")]
public class GetUserQuery : IQuery<UserDto>
{
    public int Id { get; set; }
}
```

**Implement handler:**
```csharp
[CqrsHandler]
public class GetUserHandler : IQueryHandler<GetUserQuery, UserDto> { ... }
```

**Register gRPC server:**
```csharp
builder.Services.AddLinKitGrpcServer();
app.MapGrpcService<LinKitMyService>();
```

### 2. gRPC Client

**Mark your CQRS request:**
```csharp
[GrpcClient(typeof(MyServiceClient), "GetUserAsync")]
public class GetUserQuery : IQuery<UserDto>
{
    public int Id { get; set; }
}
```

**Register gRPC client:**
```csharp
builder.Services.AddLinKitGrpcClient();
builder.Services.AddSingleton<IGrpcChannelProvider, MyChannelProvider>();
```

**Send request via mediator:**
```csharp
var result = await mediator.QueryAsync(new GetUserQuery { Id = 1 });
```

### 3. Channel Provider Example

```csharp
public class MyChannelProvider : IGrpcChannelProvider
{
    public GrpcChannel GetChannel(Type serviceClientType)
        => GrpcChannel.ForAddress("https://localhost:5001");
}
```

## Configuration

- Use `[GrpcEndpoint]` on CQRS requests to expose them as gRPC server methods.
- Use `[GrpcClient]` to generate gRPC client calls for remote services.
- All mapping between CQRS requests/responses and gRPC messages is automatic.
- For custom mapping, use the Mapping Kit from LinKit.Core.

## License

MIT
