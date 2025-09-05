# LinKit.Core

[![NuGet Version](https://img.shields.io/nuget/v/LinKit.Core.svg)](https://www.nuget.org/packages/LinKit.Core/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/LinKit.Core.svg)](https://www.nuget.org/packages/LinKit.Core/)

**LinKit.Core** is a high-performance, modular toolkit for .NET, providing source-generated helpers for CQRS, Dependency Injection, Minimal API Endpoints, Mapping, Messaging, and gRPC. LinKit eliminates boilerplate, maximizes runtime performance, and is fully compatible with NativeAOT and trimming.

---

## Why LinKit?

Most .NET libraries rely on runtime reflection, which is slow, memory-intensive, and incompatible with NativeAOT. LinKit uses C# Source Generators to analyze your code and generate optimized, boilerplate-free C# at compile time, linking your application's components together.

**Key Benefits:**
- 🚀 **Zero Reflection:** No runtime scanning or reflection.
- ⚡ **Fast Startup:** No assembly scanning.
- 🗑️ **AOT & Trimming Safe:** Works with Blazor, MAUI, NativeAOT.
- ✍️ **Clean API:** Intent-driven, explicit, and easy to use.
- 🤖 **Automated Boilerplate:** For DI, API endpoints, gRPC, messaging, and mapping.

---

## LinKit Ecosystem

| Package | Description | NuGet |
| ------- | ----------- | ----- |
| `LinKit.Core` | **Required.** Interfaces, attributes, and source generator. | [NuGet](https://www.nuget.org/packages/LinKit.Core/) |
| `LinKit.Grpc` | gRPC server/client codegen for CQRS requests. | [NuGet](https://www.nuget.org/packages/LinKit.Grpc/) |
| `LinKit.Messaging.RabbitMQ` | RabbitMQ implementation for Messaging Kit. | [NuGet](https://www.nuget.org/packages/LinKit.Messaging.RabbitMQ/) |
| `LinKit.Messaging.Kafka` | Kafka implementation for Messaging Kit. | [NuGet](https://www.nuget.org/packages/LinKit.Messaging.Kafka/) |

---

## Installation

```shell
dotnet add package LinKit.Core
```
Add other packages as needed:
```shell
dotnet add package LinKit.Grpc
dotnet add package LinKit.Messaging.RabbitMQ
```

---

## Kits Overview

### 1. CQRS Kit

A source-generated Mediator for the CQRS pattern.

- **Define Requests:** Implement `ICommand`, `ICommand<TResult>`, or `IQuery<TResult>`.
- **Create Handlers:** Implement the handler and mark with `[CqrsHandler]`.
- **Register:** `builder.Services.AddLinKitCqrs();`

```csharp
public class GetUserQuery : IQuery<UserDto>
{
    public int Id { get; set; }
}

[CqrsHandler]
public class GetUserHandler : IQueryHandler<GetUserQuery, UserDto>
{
    public Task<UserDto> Handle(GetUserQuery query, CancellationToken ct) { ... }
}
```

**Usage:**
```csharp
builder.Services.AddLinKitCqrs();
var user = await mediator.QueryAsync(new GetUserQuery { Id = 1 });
```

---

### 2. Dependency Injection Kit

Attribute-based, source-generated DI registration.

- **Mark Services:** `[RegisterService(Lifetime.Scoped)]` on your class.
- **Register:** `builder.Services.AddLinKitDependency();`

```csharp
[RegisterService(Lifetime.Scoped)]
public class MyService : IMyService { ... }
```

**Usage:**
```csharp
builder.Services.AddLinKitDependency();
```

---

### 3. Endpoints Kit (Minimal APIs)

Source-generates Minimal API endpoints from CQRS requests.

- **Decorate Requests:** `[ApiEndpoint]` on your command/query.
- **Property Binding:** Use `[FromRoute]`, `[FromQuery]`, etc.
- **Register:** `app.MapGeneratedEndpoints();`

```csharp
[ApiEndpoint(ApiMethod.GET, "users/{Id}")]
public class GetUserQuery : IQuery<UserDto>
{
    [FromRoute] public int Id { get; set; }
}
```

**Usage:**
```csharp
app.MapGeneratedEndpoints();
```

---

### 4. Mapping Kit

A reflection-free, source-generated object mapper.

- **Create Mapper Context:** `[MapperContext]` partial class implementing `IMappingConfigurator`.
- **Configure Mappings:** Use `builder.CreateMap<TSrc, TDest>()` and `.ForMember(...)`.
- **No DI Required:** Just use the generated extension methods.

```csharp
[MapperContext]
public partial class AppMapperContext : IMappingConfigurator
{
    public void Configure(IMappingBuilder builder)
    {
        builder.CreateMap<User, UserDto>()
            .ForMember(dest => dest.Name, src => src.UserName);
    }
}
```

**Usage:**
```csharp
var dto = userEntity.ToUserDto();
var dtos = userEntities.ToUserDtoList();
```

**Mapping Conventions:**
1. Explicit `.ForMember()` config
2. Name matching (case-insensitive)
3. `[JsonPropertyName]`/`[JsonProperty]` attribute matching

---

### 5. Messaging Kit

Source-generated publisher/consumer for message brokers (RabbitMQ, Kafka).

- **Mark Messages:** `[Message]` on your event/command.
- **Write Handlers:** `[CqrsHandler]` for the message.
- **Register:** `builder.Services.AddLinKitMessaging();` and the broker package.

```csharp
[Message("user-events", RoutingKey = "user.created", QueueName = "email-service-queue")]
public record UserCreatedEvent(int UserId, string Email);

[CqrsHandler]
public class UserCreatedHandler : ICommandHandler<UserCreatedEvent> { ... }
```

**Publisher:**
```csharp
builder.Services.AddLinKitMessaging();
builder.Services.AddLinKitRabbitMQ(configuration);
// await publisher.PublishAsync(new UserCreatedEvent(...));
```

**Consumer:**
```csharp
builder.Services.AddLinKitCqrs();
builder.Services.AddLinKitMessaging();
builder.Services.AddLinKitRabbitMQ(configuration);
```

---

### 6. gRPC Kit (via LinKit.Grpc)

Source-generates gRPC server and client code for CQRS requests.

**Server:**
- `[GrpcEndpoint(typeof(MyServiceBase), "MethodName")]` on CQRS request.
- Handler: `[CqrsHandler]`
- Register: `builder.Services.AddLinKitGrpcServer();` and `app.MapGrpcService<LinKitMyService>();`

```csharp
[GrpcEndpoint(typeof(UserService.UserServiceBase), "GetUserById")]
public class GetUserQuery : IQuery<UserDto> { ... }
```

**Client:**
- `[GrpcClient(typeof(MyServiceClient), "MethodNameAsync")]` on CQRS request.
- Register: `builder.Services.AddLinKitGrpcClient();` and `IGrpcChannelProvider`.

```csharp
[GrpcClient(typeof(UserService.UserClient), "GetUserByIdAsync")]
public class GetUserQuery : IQuery<UserDto> { ... }
```

**Usage:**
```csharp
var user = await mediator.QueryAsync(new GetUserQuery { Id = 1 });
```

---

## AOT & Trimming

LinKit is fully compatible with NativeAOT and trimming. For best results, use `System.Text.Json` source generation for DTOs and messages.

---

## Advanced Configuration

- All `AddLinKit...()` methods are additive and can be combined.
- No manual registration of handlers or mappings is needed.
- For custom mapping, use the Mapping Kit.
- For custom gRPC channel, implement `IGrpcChannelProvider`.

---

## Contributing

Contributions, issues, and feature requests are welcome!

---