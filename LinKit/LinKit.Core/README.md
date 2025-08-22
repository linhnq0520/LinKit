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
*   🤖 **Automated Boilerplate:** Reduces repetitive code for DI registrations and API endpoints.

## Features

The `LinKit.Core` package provides a collection of "kits" designed to solve common problems.

### 1. The CQRS Kit
A source-generated Mediator implementation for the CQRS pattern with a clear, explicit API.

### 2. The Dependency Injection Kit
An automatic dependency injection registration system using attributes.

### 3. The Endpoints Kit
Automatically generates Minimal API endpoints directly from your Command and Query definitions.

## Installation

Install the core package from NuGet:
```shell
dotnet add package LinKit.Core
```

---

## Feature 1: The CQRS Kit

A fast, AOT-safe way to implement the CQRS pattern.

### Step 1: Define Your Commands & Queries

Create records or classes that implement `ICommand`, `ICommand<TResult>`, or `IQuery<TResult>`.

```csharp
// Features/Users/GetUser.cs
using LinKit.Core.Cqrs;

public record GetUserQuery(int Id) : IQuery<UserDto>;
public record UserDto(int Id, string Name);
```

### Step 2: Create Handlers

Implement the corresponding handler and **mark it with the `[CqrsHandler]` attribute.**

```csharp
// Features/Users/GetUserHandler.cs
using LinKit.Core.Cqrs;

[CqrsHandler] // <-- Crucial attribute for discovery!
public class GetUserQueryHandler : IQueryHandler<GetUserQuery, UserDto>
{
    public Task<UserDto> HandleAsync(GetUserQuery query, CancellationToken ct)
    {
        var user = new UserDto(query.Id, "Awesome LinKit User");
        return Task.FromResult(user);
    }
}
```

### Step 3: Register CQRS Services

In `Program.cs`, call the `AddLinKitCqrs()` extension method.

```csharp
// Program.cs
using LinKit.Core;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddLinKitCqrs();
// ...
```

---

## Feature 2: The Dependency Injection Kit

Tired of manually registering your services? Let the generator do it for you.

### Step 1: Mark Your Services

Decorate your classes with the `[RegisterService]` attribute. The generator will infer the service type from the first implemented interface if not specified.

```csharp
// Services/MyScopedService.cs
using LinKit.Core.Abstractions;

public interface IMyService { /* ... */ }

[RegisterService(Lifetime.Scoped)]
public class MyScopedService : IMyService { /* ... */ }
```

### Step 2: Register Generated Services

In `Program.cs`, call the `AddGeneratedServices()` extension method.

```csharp
// Program.cs
using LinKit.Core;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddGeneratedServices();
// ...
```

---

## Feature 3: The Endpoints Kit

**Automatically generate Minimal API endpoints from your CQRS requests.** This keeps your `Program.cs` clean and co-locates your API definition with your request logic.

### Step 1: Decorate Your Commands & Queries

Add the `[ApiEndpoint]` attribute to any command or query you want to expose as an HTTP endpoint. Use `[FromRoute]`, `[FromQuery]`, etc., on properties to control model binding.

**Example 1: GET with a route parameter**
```csharp
using LinKit.Core.Cqrs;
using LinKit.Core.Endpoints;

[CqrsHandler] // Still needed for the handler
[ApiEndpoint(ApiMethod.Get, "users/{Id}")] // Expose as GET /api/users/{Id}
public record GetUserQuery : IQuery<UserDto>
{
    [FromRoute] // Bind "Id" from the route
    public int Id { get; init; } 
}
```

**Example 2: POST with a request body**
```csharp
using LinKit.Core.Cqrs;
using LinKit.Core.Endpoints;

[CqrsHandler]
[ApiEndpoint(ApiMethod.Post, "users")] // Expose as POST /api/users
public record CreateUserCommand(string Name, string Email) : ICommand<int>;
```

### Step 2: Map the Generated Endpoints

In `Program.cs`, call the `MapGeneratedEndpoints()` extension method on the `WebApplication` instance.

```csharp
// Program.cs
using LinKit.Core;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddLinKitCqrs();
builder.Services.AddGeneratedServices();

var app = builder.Build();

// This one line maps all endpoints marked with [ApiEndpoint]
app.MapGeneratedEndpoints();

app.Run();
```
That's it! Your API endpoints are now live, fully integrated with the CQRS mediator, without a single `app.MapGet()` call in your `Program.cs`.

## How It Works

The `LinKit.Core` package includes a powerful source generator that acts as the magic behind the scenes. When you build your project:
1.  It scans your code for `[CqrsHandler]`, `[RegisterService]`, and `[ApiEndpoint]` attributes.
2.  It generates new C# files containing:
    *   The `AddLinKitCqrs()` and `AddGeneratedServices()` methods with all necessary DI registrations.
    *   The `MapGeneratedEndpoints()` method, which contains all the `MapGet`, `MapPost`, etc., calls.
    *   (Previously) Explicit extension methods for `IMediator`.
3.  These generated files are seamlessly included in your project's compilation.

This compile-time approach ensures maximum performance, type safety, and compatibility, making your application faster, smaller, and more robust.

## Contributing

Contributions, issues, and feature requests are welcome.