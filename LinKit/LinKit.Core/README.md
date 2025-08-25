# LinKit

[![NuGet Version](https://img.shields.io/nuget/v/LinKit.Core.svg)](https://www.nuget.org/packages/LinKit.Core/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/LinKit.Core.svg)](https://www.nuget.org/packages/LinKit.Core/)

**LinKit** is a toolkit of high-performance, source-generated helpers for modern .NET. It's designed to boost runtime performance, reduce application startup time, and ensure compatibility with advanced .NET features like AOT (Ahead-Of-Time) compilation and assembly trimming.

The name "LinKit" is a play on "Link It", reflecting the library's role in connecting application components at compile time, and is also a nod to the author's name, Linh.

## Why LinKit?

Many popular .NET libraries rely on runtime reflection. While powerful, reflection can be slow, memory-intensive, and is incompatible with technologies like NativeAOT that rely on static analysis.

**LinKit takes a different approach.** It uses C# Source Generators to analyze your code at compile time and generate highly optimized, boilerplate-free C# code that "links" your application's components together.

**Benefits for ALL .NET Projects:**
*   🚀 **Blazing Fast Performance:** Dispatches requests directly without any runtime reflection overhead.
*   ⏱️ **Faster Startup:** No need for slow assembly scanning when your application starts.
*   🗑️ **Trimming & AOT Safe:** Perfect for Blazor WASM, MAUI, and NativeAOT applications.
*   ✍️ **Clean & Explicit API:** A clear, intent-driven API that encourages good CQRS and API design.
*   🤖 **Automated Boilerplate:** Reduces repetitive code for DI registrations, Minimal API endpoints, and gRPC services.

## Features

The `LinKit.Core` package provides a collection of "kits" designed to solve common problems.

1.  **The CQRS Kit:** A source-generated Mediator for the CQRS pattern.
2.  **The Dependency Injection Kit:** Automatic DI registration using attributes.
3.  **The Endpoints Kit:** Auto-generates Minimal API endpoints from CQRS requests.
4.  **The gRPC Kit:** Auto-generates gRPC service implementations from CQRS requests.

## Installation

Install the core package from NuGet:
```shell
dotnet add package LinKit.Core
```

---

## The Kits in Detail

### 1. The CQRS Kit

A fast, AOT-safe way to implement the CQRS pattern.

*   **Define Requests:** Create records/classes implementing `ICommand`, `ICommand<TResult>`, or `IQuery<TResult>`.
*   **Create Handlers:** Implement the corresponding handler and **mark it with `[CqrsHandler]`**.
*   **Register:** Call `builder.Services.AddLinKitCqrs()` in `Program.cs`.

```csharp
// GetUserQuery.cs
[CqrsHandler]
public record GetUserQuery(int Id) : IQuery<UserDto>;

// GetUserQueryHandler.cs
public class GetUserQueryHandler : IQueryHandler<GetUserQuery, UserDto>
{
    public Task<UserDto> HandleAsync(GetUserQuery query, CancellationToken ct)
    {
        // ... business logic ...
        return Task.FromResult(new UserDto(query.Id, "Awesome LinKit User"));
    }
}
```

### 2. The Dependency Injection Kit

Tired of manually registering services?

*   **Mark Services:** Decorate your classes with `[RegisterService(Lifetime.Scoped)]`.
*   **Register:** Call `builder.Services.AddGeneratedServices()` in `Program.cs`.

```csharp
// MyService.cs
using LinKit.Core.Abstractions;

public interface IMyService { /* ... */ }

[RegisterService(Lifetime.Scoped)]
public class MyService : IMyService { /* ... */ }
```

### 3. The Endpoints Kit (for Minimal APIs)

**Automatically generate Minimal API endpoints from your CQRS requests.**

*   **Decorate Requests:** Add `[ApiEndpoint]` to your command/query. Use `[FromRoute]`, `[FromQuery]` on properties to control binding.
*   **Map Endpoints:** Call `app.MapGeneratedEndpoints()` in `Program.cs`.

```csharp
// GetUserQuery.cs
using LinKit.Core.Endpoints;

[CqrsHandler]
[ApiEndpoint(ApiMethod.GET, "users/{Id}")] // Exposes GET /api/users/{Id}
public record GetUserQuery : IQuery<UserDto>
{
    [FromRoute] public int Id { get; init; } 
}
```
```csharp
// Program.cs
var app = builder.Build();
app.MapGeneratedEndpoints(); // Maps all [ApiEndpoint] requests
app.Run();
```

### 4. The gRPC Kit

**Automatically generate gRPC service implementations from your CQRS requests,** turning your gRPC services into thin, clean adapters.

#### Step 1: Define your `.proto` file

Define your service and messages as you normally would with gRPC.
```protobuf
// Protos/users.proto
syntax = "proto3";
option csharp_namespace = "SampleWebApp.Grpc.Users";

service UserService {
  rpc GetUser (GetUserRequest) returns (GetUserResponse);
}
message GetUserRequest { int32 id = 1; }
message User { int32 id = 1; string name = 2; }
message GetUserResponse { User user = 1; }
```

#### Step 2: Decorate Your CQRS Request

Add the `[GrpcEndpoint]` attribute to your corresponding command or query, linking it to the gRPC service and method.

```csharp
// Features/Users/GetUserQuery.cs
using LinKit.Core.Cqrs;
using LinKit.Core.Grpc;
using SampleWebApp.Grpc.Users; // Namespace from the .proto file

[CqrsHandler]
[GrpcEndpoint(typeof(UserService.UserServiceBase), "GetUser")] // Link to the gRPC definition
public record GetUserQuery(int Id) : IQuery<UserDto?>;
```
*Note: LinKit automatically maps properties with matching names between your gRPC messages and your CQRS records/classes.*

#### Step 3: Register and Map the Generated Service

In `Program.cs`, the `[RegisterService]` attribute on the generated gRPC service handles DI. You just need to map the service.

```csharp
// Program.cs
using SampleWebApp.Grpc.Users; // Contains the generated service

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddGrpc();
builder.Services.AddLinKitCqrs();
builder.Services.AddGeneratedServices(); // This will register the generated gRPC service

var app = builder.Build();

// Map the service implementation that LinKit generated for you
app.MapGrpcService<LinKitUserService>(); 

app.Run();
```
That's it! Your gRPC endpoint is now live, and all incoming requests are automatically routed through your CQRS pipeline, keeping your business logic clean and separate from the transport layer.

## How It Works

The `LinKit.Core` package includes a powerful source generator. When you build your project, it scans for `[CqrsHandler]`, `[RegisterService]`, `[ApiEndpoint]`, and `[GrpcEndpoint]` attributes and generates the necessary boilerplate code—DI registrations, Mediator pipelines, Minimal API endpoints, and gRPC service implementations—all at compile time.

This approach ensures maximum performance, type safety, and compatibility, making your application faster, smaller, and more robust.

## Contributing

Contributions, issues, and feature requests are welcome.