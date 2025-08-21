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
*   ✍️ **Clean & Explicit API:** A clear, intent-driven API that encourages good CQRS practices.
*   🤖 **Automated Boilerplate:** Reduces repetitive code like DI registrations.

## Features

The `LinKit.Core` package provides a collection of "kits" designed to solve common problems.

### 1. The CQRS Kit
A source-generated Mediator implementation for the CQRS pattern with a clear, explicit API.

### 2. The Dependency Injection Kit
An automatic dependency injection registration system using attributes, eliminating manual `services.Add...()` calls.

## Installation

Install the core package from NuGet:
```shell
dotnet add package LinKit.Core
```

## Feature 1: The CQRS Kit

A fast, AOT-safe way to implement the CQRS pattern with a clear separation of concerns.

### Step 1: Define Your Commands and Queries

Create records or classes that implement the specific interfaces:
*   `ICommand`: An action that modifies state and does not return a value.
*   `ICommand<TResult>`: An action that modifies state and returns a value.
*   `IQuery<TResult>`: An action that only reads data and returns a value.

```csharp
// Features/Users/GetUser.cs
using LinKit.Core.Cqrs;

// A query that returns a UserDto
public record GetUserQuery(int Id) : IQuery<UserDto>;

public record UserDto(int Id, string Name);
```

### Step 2: Create Handlers

Implement the corresponding handler interface (`ICommandHandler<T>` or `IQueryHandler<T>`). **Mark each handler with the `[CqrsHandler]` attribute.**

```csharp
// Features/Users/GetUserHandler.cs
using LinKit.Core.Cqrs;

// The handler for the query
[CqrsHandler] // <-- Crucial attribute for discovery!
public class GetUserQueryHandler : IQueryHandler<GetUserQuery, UserDto>
{
    public Task<UserDto> HandleAsync(GetUserQuery query, CancellationToken ct)
    {
        var user = new UserDto(query.Id, "Awesome LinKit User");
        // Task.FromResult is highly optimized for synchronous, completed results.
        return Task.FromResult(user);
    }
}
```

### Step 3: Register the CQRS System

In `Program.cs`, call the `AddLinKitCqrs()` extension method.

```csharp
// Program.cs
using LinKit.Core; // This namespace contains the extension methods

var builder = WebApplication.CreateBuilder(args);

// Registers the mediator and all [CqrsHandler] marked handlers
builder.Services.AddLinKitCqrs();

var app = builder.Build();
```

### Step 4: Dispatch Requests

Inject `IMediator` and use the explicit `QueryAsync` and `SendAsync` methods.

```csharp
// Program.cs
using LinKit.Core.Cqrs;
using Microsoft.AspNetCore.Mvc;

app.MapGet("/users/{id}", async (int id, [FromServices] IMediator mediator, CancellationToken ct) =>
{
    // Use QueryAsync for queries
    var user = await mediator.QueryAsync(new GetUserQuery(id), ct);
    return Results.Ok(user);
});

app.MapPost("/users", async (CreateUserCommand command, [FromServices] IMediator mediator, CancellationToken ct) =>
{
    // Use SendAsync for commands
    await mediator.SendAsync(command, ct); 
    return Results.Ok();
});
```

## Feature 2: The Dependency Injection Kit

Tired of manually registering your services? Let the generator do it for you.

### Step 1: Mark Your Services

Decorate your classes with the `[RegisterService]` attribute. You can specify the `Lifetime`. If you don't specify a `ServiceType`, the generator will automatically infer the first implemented interface.

```csharp
// Services/MyScopedService.cs
using LinKit.Core.Abstractions;

public interface IMyService { /* ... */ }

// The generator will infer IMyService as the service type.
[RegisterService(Lifetime.Scoped)]
public class MyScopedService : IMyService
{
    // ...
}
```

### Step 2: Register Generated Services

In `Program.cs`, call the `AddGeneratedServices()` extension method.

```csharp
// Program.cs
using LinKit.Core;

var builder = WebApplication.CreateBuilder(args);

// ... other services ...

// Scans and registers all services marked with [RegisterService]
builder.Services.AddGeneratedServices();

var app = builder.Build();
```
That's it! All your marked services are now correctly registered in the DI container without any manual effort.

## How It Works

The `LinKit.Core` package includes a powerful source generator that acts as the magic behind the scenes. When you build your project:
1.  It scans your code for `[CqrsHandler]` and `[RegisterService]` attributes.
2.  It generates new C# files containing:
    *   Explicit extension methods (`SendAsync`, `QueryAsync`) for each of your requests.
    *   The `AddLinKitCqrs()` and `AddGeneratedServices()` methods with all necessary DI registrations.
3.  These generated files are seamlessly included in your project's compilation.

This compile-time approach ensures maximum performance, type safety, and compatibility, making your application faster, smaller, and more robust.

## Contributing

Contributions, issues, and feature requests are welcome.