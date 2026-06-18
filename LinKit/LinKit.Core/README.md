# LinKit.Core

[![NuGet Version](https://img.shields.io/nuget/v/LinKit.Core.svg)](https://www.nuget.org/packages/LinKit.Core/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/LinKit.Core.svg)](https://www.nuget.org/packages/LinKit.Core/)

**LinKit.Core** is a high-performance, modular toolkit for .NET, providing source-generated helpers for CQRS, Dependency Injection, Minimal API Endpoints, Background Jobs, Mapping, Messaging, and gRPC. LinKit eliminates boilerplate, maximizes runtime performance, and is fully compatible with NativeAOT and trimming.

---

## Why LinKit?

Most .NET libraries rely on runtime reflection, which is slow, memory-intensive, and incompatible with NativeAOT. LinKit uses C# Source Generators to analyze your code and generate optimized, boilerplate-free C# at compile time, linking your application's components together.

**Key Benefits:**

- **Zero Reflection:** No runtime scanning or reflection.
- **Fast Startup:** No assembly scanning.
- **AOT & Trimming Safe:** Works with Blazor, MAUI, NativeAOT.
- **Clean API:** Intent-driven, explicit, and easy to use.
- **Automated Boilerplate:** For DI, API endpoints, background jobs, gRPC, messaging, and mapping.

---

## LinKit Ecosystem

| Package                     | Description                                                 | NuGet                                                              |
| --------------------------- | ----------------------------------------------------------- | ------------------------------------------------------------------ |
| `LinKit.Core`               | **Required.** Interfaces, attributes, and source generator. | [NuGet](https://www.nuget.org/packages/LinKit.Core/)               |
| `LinKit.Grpc`               | gRPC server/client codegen for CQRS requests.               | [NuGet](https://www.nuget.org/packages/LinKit.Grpc/)               |
| `LinKit.Messaging.RabbitMQ` | RabbitMQ implementation for Messaging Kit.                  | [NuGet](https://www.nuget.org/packages/LinKit.Messaging.RabbitMQ/) |
| `LinKit.Messaging.Kafka`    | Kafka implementation for Messaging Kit.                     | [NuGet](https://www.nuget.org/packages/LinKit.Messaging.Kafka/)    |

---

## Installation

```shell
dotnet add package LinKit.Core
```

Add other packages as needed:

```shell
dotnet add package LinKit.Grpc
dotnet add package LinKit.Messaging.RabbitMQ
dotnet add package LinKit.Messaging.Kafka
```

---

## Kits Overview

### 1. CQRS Kit

A high-performance, source-generated Mediator for implementing the CQRS (Command Query Responsibility Segregation) pattern with **zero reflection**.

The LinKit CQRS Kit analyzes your code at compile time to generate a highly optimized `Mediator`. It supports modular registration, advanced pipeline behaviors, and is fully compatible with NativeAOT.

#### Core Interfaces

-   `ICommand`: A "void" operation (returns `Task<Unit>`).
-   `ICommand<TResponse>`: An operation that changes state and returns a value.
-   `IQuery<TResponse>`: A data retrieval operation.

---

#### Step 1: Define Requests & Handlers

**1. Create a Request:**
```csharp
public record CreateUserCommand(string Name) : ICommand<int>;
```

**2. Create a Handler:**
Mark your handler with `[CqrsHandler]` for automatic discovery.
```csharp
[CqrsHandler]
public class CreateUserHandler : ICommandHandler<CreateUserCommand, int>
{
    public async Task<int> HandleAsync(CreateUserCommand request, CancellationToken ct) 
        => await Task.FromResult(1);
}
```

---

#### Step 2: Multi-Assembly Registration (`[CqrsContext]`)

In real-world applications (Clean Architecture, Modular Monoliths), your requests, handlers, and pipeline behaviors usually reside in different class libraries (e.g., `Application.dll`, `Infrastructure.dll`). Since Source Generators run *per-project*, generating multiple `IMediator` instances across different projects would cause Dependency Injection conflicts.

LinKit solves this elegantly with `[CqrsContext]`. It acts as a **centralized registry** using **Assembly Marker Types**. You define it once in your Host project (e.g., `WebAPI`), and the generator will automatically dive into the referenced assemblies, discover all handlers **AND pipeline behaviors**, and generate a single, unified, highly-optimized Mediator.

**1. Create Marker Classes in your target projects:**
Create an empty class in any project that contains your CQRS logic. This class acts as an anchor for the generator.

```csharp
// In your Application layer project
namespace MyApp.Application;
public sealed class ApplicationAssemblyMarker { }

// In your Infrastructure layer project
namespace MyApp.Infrastructure;
public sealed class InfrastructureAssemblyMarker { }
```

**2. Define the Context in your Host project (WebAPI):**
In your startup project, apply `[CqrsContext]` to any class (even `Program.cs`) and pass the marker types.

```csharp
using LinKit.Core.Cqrs;
using MyApp.Application;
using MyApp.Infrastructure;

// The LinKit source generator running in the WebAPI project will read this,
// scan the entire Application and Infrastructure assemblies, 
// and wire up ALL discovered Handlers AND Pipeline Behaviors automatically!
[CqrsContext(
    typeof(ApplicationAssemblyMarker),
    typeof(InfrastructureAssemblyMarker)
)]
public partial class GlobalCqrsRegistry 
{ 
}
```

*Note: You can still use `[CqrsHandler]` or `[CqrsBehavior]` on individual classes for explicit local discovery in the host project. The generator will seamlessly combine local classes with the context-discovered ones.*

--- 

#### Step 3: Advanced Pipeline Behaviors

LinKit's pipeline is generated at compile-time using a **Clean Name matching engine**. This means a behavior targeting `ICommand` will automatically match both `ICommand` and `ICommand<TResponse>`.

*(Tip: If your behaviors are located in external assemblies, just ensure their assembly marker is included in your `[CqrsContext]` as shown in Step 2. LinKit will find them automatically!)*

##### A. Global & Contract Behaviors (`[CqrsBehavior]`)
Use this for cross-cutting concerns. You can target a specific marker interface or use `typeof(object)` for a truly global behavior.

```csharp
// 1. Truly Global Behavior (Runs for EVERYTHING: Commands and Queries)
[CqrsBehavior(typeof(IRequest<>), Order = 1)]
public class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> 
    where TRequest : IRequest<TResponse>
{ ... }

// 2. Contract-based Behavior (Runs only for Commands)
// LinKit automatically matches ICommand, ICommand<T>, and their implementations.
[CqrsBehavior(typeof(ICommand), Order = 2)]
public class TransactionBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : ICommand<TResponse>
{ ... }
```

##### B. Specific Behaviors (`[ApplyBehavior]`)
Use this for ad-hoc logic applied directly to a specific request class. These behaviors run **closest to the handler**.

```csharp
[ApplyBehavior(typeof(ValidationBehavior<,>), typeof(IdempotencyBehavior<,>))]
public record ProcessPaymentCommand(Guid Id) : ICommand<bool>;
```

**Execution Order:**
1. Global Behaviors (by `Order`)
2. Contract Behaviors (by `Order`)
3. Specific Behaviors (defined in `[ApplyBehavior]`)
4. Final Handler

---

#### Step 4: Register and Use

**Registration:**
```csharp
// Program.cs
// This single call registers the generated Mediator, all Handlers, 
// and wires up the entire Pipeline.
builder.Services.AddLinKitCqrs();
```

**Usage:**
```csharp
public class MyApi(IMediator mediator)
{
    public async Task Invoke()
    {
        // Use SendAsync for ICommand / ICommand<T>
        int userId = await mediator.SendAsync(new CreateUserCommand("Alice"));

        // Use QueryAsync for IQuery<T>
        var user = await mediator.QueryAsync(new GetUserQuery(userId));
    }
}
```

---

#### Notifications (`INotification`)

LinKit also supports publish/subscribe style notifications through `INotification` and `INotificationHandler<TNotification>`.

```csharp
public record UserCreatedNotification(int UserId) : INotification;

[CqrsHandler]
public class SendWelcomeEmailHandler : INotificationHandler<UserCreatedNotification>
{
    public Task HandleAsync(UserCreatedNotification notification, CancellationToken ct)
        => Task.CompletedTask;
}

[CqrsHandler]
public class PushAuditLogHandler : INotificationHandler<UserCreatedNotification>
{
    public Task HandleAsync(UserCreatedNotification notification, CancellationToken ct)
        => Task.CompletedTask;
}
```

Publish from `IMediator`:

```csharp
await mediator.PublishAsync(new UserCreatedNotification(userId)); // default: Sequential
await mediator.PublishAsync(new UserCreatedNotification(userId), PublishStrategy.Parallel);
```

**Current notification behavior:**

- Default strategy is `Sequential` (handlers run one-by-one).
- `Parallel` runs all handlers concurrently (`Task.WhenAll`).
- Pipeline behaviors (`IPipelineBehavior<,>`) apply to command/query, not notification.
- If a notification type has no discovered handler (`[CqrsHandler]` or `[CqrsContext]`), publish is a no-op (`Task.CompletedTask`).

---

## 2. Dependency Injection Kit

### Usage Guide

#### 1. Mark Your Services
Use the `[RegisterService]` attribute to mark a class for automatic registration in the DI Container.

```csharp
// Automatically registers as IUserService (the Leaf Interface) with Scoped lifetime
[RegisterService(Lifetime.Scoped)]
public class UserService : IUserService { ... }

// Register with a specific interface and Singleton lifetime
[RegisterService(Lifetime.Singleton, ServiceType = typeof(IDataProvider))]
public class MyDataProvider : IDataProvider, IDisposable { ... }

// Keyed Service registration (Native .NET 8+ support)
[RegisterService(Lifetime.Transient, Key = "excel_report")]
public class ExcelReportService : IReportService { ... }
```

#### 2. Open Generics Support
To register Open Generic types, set the `IsGeneric` property to `true`.

```csharp
[RegisterService(Lifetime.Scoped, IsGeneric = true)]
public class Repository<T> : IRepository<T> where T : class { ... }
```

#### 3. Automatic Interface Discovery
If you do not specify a `ServiceType`, LinKit follows a smart hierarchy to choose the best interface:
1. **Convention:** An interface matching the class name (e.g., `MyService` -> `IMyService`).
2. **Leaf Interface:** The most specific interface in the inheritance chain (closest parent).
3. **Self-Registration:** If no interfaces are found, it registers the class itself.

#### 4. Activation in Program.cs
The Source Generator automatically creates an extension method called `AddLinKitDependency()`. Call it once in your startup configuration:

```csharp
var builder = WebApplication.CreateBuilder(args);

// Automatically registers all classes marked with [RegisterService]
builder.Services.AddLinKitDependency();

var app = builder.Build();
```

### `RegisterServiceAttribute` Parameters

| Parameter | Type | Description |
| :--- | :--- | :--- |
| `Lifetime` | `int`/`Enum` | `0`: Scoped, `1`: Singleton, `2`: Transient (based on your Enum definition). |
| `ServiceType` | `Type` | (Optional) Specify a specific interface/base class to register. |
| `Key` | `string` | (Optional) Registers the service as a **Keyed Service**. |
| `IsGeneric` | `bool` | (Optional) Set to `true` for Open Generic classes (`Class<T>`). |

### Why LinKit DI?

In standard DI registration, if a class implements multiple inherited interfaces:
```csharp
public interface IRepository<T> { }
public interface IUserRepository : IRepository<User> { }
public class UserRepository : IUserRepository { }
```
LinKit is "context-aware"—it understands that you likely want to inject `IUserRepository` rather than the generic `IRepository<User>`, ensuring your dependencies are specific and clean.

---

### Pro-Tips:
* **Multiple Registrations:** If a class implements multiple independent interfaces (e.g., `IServiceA` and `IServiceB`), you can apply the attribute multiple times (requires `AllowMultiple = true` in attribute definition).
* **Native AOT:** Since all code is generated at compile-time, this library is perfect for high-performance, cloud-native environments where Reflection is restricted.

--- 

### 3. Endpoints Kit (Minimal APIs)

The Endpoints Kit transforms your CQRS requests directly into high-performance Minimal API endpoints using Source Generators. It eliminates controllers, manual route mapping, and reflection-based dispatching.

-   **Zero Boilerplate:** The request DTO *is* the API definition.
-   **Automatic Binding:** Infers `[FromBody]` for command payloads (POST/PUT/PATCH) and `[AsParameters]` for queries (GET/DELETE).
-   **Flexible Response Handling:** Intelligent result mapping that handles both Reference Types and **Value Types** (like `bool`, `int`). It automatically returns `200 OK` for valid results and `404 Not Found` for nulls without boxing overhead.
-   **Keyed Mediator Support:** Ability to inject specific Mediator instances using .NET 8+ Keyed Services.
-   **Metadata & OpenAPI:** Built-in support for Summary, Description, Tags, and Versions.
-   **Security:** Native support for Policies, Roles, and CORS.

#### Step 1: Define Endpoints

Decorate your `ICommand` or `IQuery` classes with the `[ApiEndpoint]` attribute.

**Example 1: A GET Query with Automatic Binding**

```csharp
using LinKit.Core.Endpoints;
using Microsoft.AspNetCore.Mvc;

// Maps to: GET /users/{Id}
[ApiEndpoint(ApiMethod.Get, "users/{Id}")]
public class GetUserQuery : IQuery<UserDto>
{
    [FromRoute] public int Id { get; set; }
}
```

**Example 2: A POST Command returning a Value Type (`bool`)**

LinKit's generator handles Value Types correctly. A `false` boolean result will still return `200 OK`, while only a true `null` (for nullable types) triggers `404 Not Found`.

```csharp
// Maps to: POST /users/check-email
[ApiEndpoint(ApiMethod.Post, "users/check-email")]
public class CheckEmailCommand : ICommand<bool>
{
    public string Email { get; set; }
}
```

**Example 3: Using Keyed Mediator & Security**

If your architecture uses multiple Mediator instances (e.g., for different modules or cross-cutting concerns), use `MediatorKey`.

```csharp
[ApiEndpoint(ApiMethod.Put, "users/{Id}/role",
    Name = "UpdateUserRole",
    Summary = "Updates user role",
    Roles = "Admin",
    MediatorKey = "IdentityMediator", // Uses [FromKeyedServices("IdentityMediator")]
    RateLimitPolicy = "Strict"
)]
public class UpdateRoleCommand : ICommand
{
    [FromRoute] public int Id { get; set; }
    public string NewRole { get; set; }
}
```

#### Step 2: Route Grouping (Optional)

Define global prefixes and shared security policies for a group of endpoints using the `[ApiRouteGroup]` attribute at the **Assembly** level.

```csharp
// In Program.cs or AssemblyInfo.cs
[assembly: ApiRouteGroup("/api/v1", Tag = "V1 API", RequireAuthorization = true)]
```

Associate an endpoint with a group using the `Group` property (it can be the prefix or the feature name):

```csharp
[ApiEndpoint(ApiMethod.Get, "products", Group = "/api/v1")]
public class GetProductsQuery : IQuery<List<ProductDto>> { ... }
```

#### Step 3: Global Exception Handling

Map custom exceptions to HTTP status codes automatically with an optimized, non-reflection based middleware.

1.  **Define Mappings:**

```csharp
using LinKit.Core.Endpoints;

[ApiExceptionMapping(StatusCodes.Status404NotFound, Title = "Resource Not Found")]
public class UserNotFoundException : Exception 
{
    public UserNotFoundException(int id) : base($"User {id} was not found.") { }
}

[ApiExceptionMapping(StatusCodes.Status409Conflict, LogLevel = "Warning")]
public class DuplicateEmailException : Exception { ... }
```

2.  **Register Middleware:**

```csharp
var app = builder.Build();
app.UseGeneratedExceptionHandler(); // Highly optimized switch-based handler
```

#### Step 4: Register Endpoints

In your `Program.cs`, simply call `MapGeneratedEndpoints`. All your CQRS-based endpoints are discovered and mapped at compile-time.

```csharp
var app = builder.Build();

app.UseGeneratedExceptionHandler(); 
app.UseAuthentication();
app.UseAuthorization();

// This single line maps all endpoints defined in your assembly
app.MapGeneratedEndpoints();

app.Run();
```

---

### 4. Background Job Kit

The Background Job Kit provides a powerful, configuration-driven system for executing CQRS commands and queries on a schedule. It source-generates the necessary infrastructure to link your job definitions to your CQRS handlers, creating a robust background processing system with zero reflection and full support for hot-reloading configurations.

#### How It Works

1.  The `[BackgroundJob]` attribute tells the source generator to map a unique, human-readable name to a specific CQRS request type.
2.  At runtime, the registered `BackgroundJobManager` (an `IHostedService`) reads your JSON configuration.
3.  It uses the generated map to find the correct CQRS request for each configured job and uses the Mediator to execute it according to the specified schedule. The entire process is type-safe and performant.
4.  When you need to run a job immediately from application code (without waiting for the next schedule), inject `IBackgroundJobTrigger` and call `TriggerAsync` with the same job name as in configuration.

#### Usage

**Step 1: Decorate a CQRS Request**

Mark any `BackgroundJobCommand` that you want to be available as a background job with the `[BackgroundJob]` attribute, providing a unique name.

```csharp
[BackgroundJob("ProcessEndOfDayReport")]
public class ProcessEndOfDayReportCommand : BackgroundJobCommand
{
}

[CqrsHandler]
public class ProcessEndOfDayReportHandler : ICommandHandler<ProcessEndOfDayReportCommand>
{
    private readonly ILogger<ProcessEndOfDayReportHandler> _logger;

    public ProcessEndOfDayReportHandler(ILogger<ProcessEndOfDayReportHandler> logger)
    {
        _logger = logger;
    }

    public Task HandleAsync(ProcessEndOfDayReportCommand command, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Processing end-of-day report of type: {Type}", command.ReportType ?? "Standard");
        // ... your business logic here ...
        return Task.CompletedTask;
    }
}
```

**Step 2: Create Your Configuration**

Create a section in your `appsettings.json` or a separate JSON file to define the schedules for your jobs. The `Name` property in the JSON **must match** the name provided in the `[BackgroundJob]` attribute.

**Example `appsettings.json`:**

```json
{
  "BackgroundJobConfig": {
    "BackgroundJobs": [
      {
        "Name": "HeartbeatCheck",
        "IsActive": true,
        "ScheduleType": "Interval",
        "TimeIntervalSeconds": 300
      },
      {
        "Name": "SendDailyNewsletter",
        "IsActive": true,
        "IsLogHistory": true,
        "RunOnStart": true,
        "ScheduleType": "Daily",
        "UtcOffsetHours": 7,
        "TimeOfDay": "08:00:00"
      },
      {
        "Name": "WeeklyDatabaseCleanup",
        "IsActive": true,
        "IsLogHistory": true,
        "ScheduleType": "Weekly",
        "TimeZoneId": "SE Asia Standard Time",
        "TimeOfDay": "03:30:00",
        "DayOfWeek": "Sunday"
      },
      {
        "Name": "ProcessEndOfDayReport",
        "IsActive": false,
        "ScheduleType": "Monthly",
        "TimeOfDay": "23:59:00",
        "DayOfMonth": 99,
        "MaxParallel": 2,
        "EmbeddedData": "{\"ReportType\": \"Financial\"}"
      }
    ]
  }
}
```

**Step 3: Register the Background Job Service**

In your `Program.cs`, call the `AddBackgroundJobs` extension method.

```csharp
var builder = WebApplication.CreateBuilder(args);

// Register Background Job hosted service + generated mapper
builder.AddBackgroundJobs("BackgroundJobsConfig.json");

var app = builder.Build();
```

This registers the `BackgroundJobManager` and `IBackgroundJobTrigger`, which will automatically start, stop, and reload jobs based on your configuration.

**Step 4 (Optional): Trigger a Job On Demand**

Use `IBackgroundJobTrigger` when you want to run a configured background job immediately from a handler, endpoint, or admin action — without changing the schedule or waiting for the next tick.

```csharp
using LinKit.Core.BackgroundJobs;

public class OrderCompletedHandler(IBackgroundJobTrigger jobTrigger)
{
    public async Task HandleAsync(OrderCompletedEvent notification, CancellationToken cancellationToken)
    {
        // Uses EmbeddedData from JSON config for this job
        await jobTrigger.TriggerAsync("ProcessEndOfDayReport", cancellationToken);

        // Or override EmbeddedData for this run only
        await jobTrigger.TriggerAsync(
            "ProcessEndOfDayReport",
            embeddedData: "{\"ReportType\": \"AdHoc\"}",
            cancellationToken);
    }
}
```

**Behavior notes:**

| Scenario | What happens |
| -------- | ------------ |
| Job is **active** (`IsActive: true`) | Runs through the live `JobRunner`, respects `MaxParallel` with scheduled runs, and does **not** reset the next scheduled delay. |
| Job exists in config but is **inactive** | Runs once via the same execution pipeline (mapper, mediator, optional history). Useful for admin "run now" on disabled jobs. |
| Job name **not in config** | Throws `InvalidOperationException`. |
| `embeddedData` argument | Pass `null` to use `EmbeddedData` from JSON; pass a string to override it for that execution only. |

> **Note:** For logic that is not tied to a configured background job, you can still call `IMediator.SendAsync` with your command directly. Use `IBackgroundJobTrigger` when you want the same path as scheduled jobs (generated mapper, `EmbeddedData`, and `IJobHistoryLogger` when `IsLogHistory` is enabled).

---

#### Configuration Details

Below is a detailed description of each property available in the job configuration.

| Property              | Type              | Description                                                                                                                                                                                            |
| --------------------- | ----------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `Name`                | `string`          | **Required.** The unique identifier for the job. This must exactly match the name provided in the `[BackgroundJob("MyUniqueJobName")]` attribute.                                                       |
| `IsActive`            | `bool`            | Determines if the job is enabled. If set to `false`, the job will not run. Changes are detected at runtime.                                                                                              |
| `IsLogHistory`        | `bool`            | Optional. If `true`, the job's execution history (start time, end time, success status, and errors) will be logged. Defaults to `false`.                                                                |
| `RunOnStart`          | `bool`            | If `true`, the job will execute once immediately upon application start (or when the job is activated via config change), and then follow its regular schedule. Defaults to `false`.                      |
| `ScheduleType`        | `string`          | **Required.** The scheduling mode. Can be one of four values: `Interval`, `Daily`, `Weekly`, `Monthly`.                                                                                                  |
| `TimeIntervalSeconds` | `int`             | Used only when `ScheduleType` is `Interval`. Defines the number of seconds to wait between each job execution.                                                                                         |
| `TimeZoneId`          | `string`          | Optional. Preferred timezone identifier (for example: `SE Asia Standard Time`, `Asia/Ho_Chi_Minh`). If valid, this takes precedence. |
| `UtcOffsetHours`      | `double`          | Optional fallback when `TimeZoneId` is not set/invalid. Supports values like `7`, `-7`, `5.5`. Valid range is `-14` to `+14`. |
| `TimeOfDay`           | `string`          | Used for `Daily`, `Weekly`, and `Monthly` schedules. Defines local run time in the resolved timezone (`TimeZoneId` -> `UtcOffsetHours` -> UTC). Format: `"HH:mm:ss"`. |
| `DayOfWeek`           | `string`          | Used only when `ScheduleType` is `Weekly`. The day of the week to run the job. Examples: `"Monday"`, `"Tuesday"`, etc.                                                                                  |
| `DayOfMonth`          | `int`             | Used only when `ScheduleType` is `Monthly`. The day of the month to run (1-31). **Special Value:** Use a large number (e.g., `99`) to signify the **last day** of the current month.                   |
| `MaxParallel`         | `int`             | The maximum number of instances of this job that can run concurrently. Defaults to `1`. Useful for long-running jobs to prevent overlap.                                                              |
| `EmbeddedData`        | `string` (JSON)   | An optional string value that is passed directly to the EmbeddedData property of your command. This can be a simple string, a JSON object, or any other format you wish to parse in your handler.      |

---

#### Execution History Logging

LinKit provides built-in support for tracking the execution status of your background jobs. When `"IsLogHistory": true` is set in the job configuration, LinKit captures the start time, end time, execution success, and any exception messages.

**1. Default File Logging**
Out of the box, LinKit logs histories to a highly optimized, memory-safe JSON-Lines file. This file is automatically created in the same directory as your configuration file (e.g., if you load `JobsConfig.json`, the history file will be `JobsConfig_History.json`).

**2. Custom Database Logging**
For enterprise applications, you likely want to store this history in a Database (SQL Server, PostgreSQL, MongoDB, etc.) instead of a file. LinKit's architecture makes this extremely easy via standard Dependency Injection.

Simply implement the `IJobHistoryLogger` interface and register it in your `Program.cs`. LinKit will automatically bypass the default file logger and use yours instead:

```csharp
using LinKit.Core.BackgroundJobs;

// 1. Create your custom logger implementation
public class MyDbJobHistoryLogger : IJobHistoryLogger
{
    private readonly ApplicationDbContext _dbContext;

    public MyDbJobHistoryLogger(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task LogAsync(JobExecutionHistory history, CancellationToken cancellationToken = default)
    {
        // Save the execution data to your database using EF Core, Dapper, etc.
        _dbContext.JobHistories.Add(history);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}

// 2. Register it in Program.cs BEFORE AddBackgroundJobs
builder.Services.AddScoped<IJobHistoryLogger, MyDbJobHistoryLogger>();

// 3. Register the Background Jobs
builder.AddBackgroundJobs("BackgroundJobsConfig.json");
```
---

### 5. Mapping Kit

A high-performance, reflection-free, source-generated object mapper. The Mapping Kit provides a fluent and type-safe API to configure mappings, which are then transformed into highly optimized, direct-assignment code at compile time.

- **Type-Safe API:** Uses lambda expressions (`dest => dest.Property`) to eliminate magic strings and catch errors at compile time.
- **Fluent Configuration:** Chain `.ForMember()` calls to create readable and maintainable mapping rules.
- **Convention-Based:** Automatically maps properties with matching names or `[JsonPropertyName]` / `[JsonProperty]` attributes.
- **No DI Required:** Generates extension methods (`.ToUserDto()`) that can be used anywhere.

#### Usage

**Step 1: Create a Mapper Context**

Create a `partial` class marked with the `[MapperContext]` attribute. This class will contain your mapping configurations.

```csharp
using LinKit.Core.Mapping;
using YourApp.Models.Entities;
using YourApp.Models.Dtos;

[MapperContext]
public partial class ApplicationMapperContext : IMappingConfigurator
{
    public void Configure(IMapperConfigurationBuilder builder)
    {
        // Define all your application's mappings here
        builder.CreateMap<User, UserDto>()
            // Examples of detailed configuration below
            ;

        builder.CreateMap<Order, OrderSummaryDto>();
    }
}
```

**Step 2: Configure Mappings with the Fluent API**

Use the `CreateMap<TSource, TDestination>()` method and chain `.ForMember()` calls to define custom mapping rules.

```csharp
// Inside the Configure method from Step 1

builder.CreateMap<User, UserDto>()
    // 1. Map from a differently named property (type-safe)
    .ForMember(dest => dest.FullName, opt => opt.MapFrom(src => src.UserName))
    
    // 2. Ignore a property
    .ForMember(dest => dest.PasswordHash, opt => opt.Ignore())
    
    // 3. Perform complex transformations
    .ForMember(dest => dest.Initials, opt => opt.MapFrom(src => $"{src.FirstName[0]}{src.LastName[0]}"))
    
    // 4. Use a custom converter method (e.g., from a static helper class)
    .ForMember(
        dest => dest.AddressString, 
        opt => opt.ConvertWith(
            typeof(AddressFormatter), 
            nameof(AddressFormatter.Format), 
            src => src.Address
        )
    );
```

**Step 3: Use the Generated Extension Methods**

The source generator automatically creates `.To...()` and `.To...List()` extension methods. Just use them directly on your objects.

```csharp
// Assuming 'user' is an instance of the User entity
var userDto = user.ToUserDto();

// For collections
// Assuming 'users' is an IEnumerable<User>
var dtoList = users.ToUserDtoList();
```

#### Mapping Conventions (Order of Precedence)

The mapper automatically maps properties that are not explicitly configured. It follows these rules in order:

1.  **Explicit Configuration:** Rules defined with `.ForMember()` are always applied first.
2.  **`[JsonPropertyName]` / `[JsonProperty]` Matching:** If a destination property has a `[JsonPropertyName]` or `[JsonProperty]` attribute, the mapper looks for a source property with the same attribute and name. This is useful for mapping between C# naming conventions and JSON/API conventions (e.g., `FullName` to `full_name`).
3.  **Name Matching:** If no attribute match is found, the mapper maps properties with the same name (case-insensitive). This includes nested objects and collections of the same type.
4.  **Nested Object Mapping:** If a property is a complex type (e.g., `Address`), and a map has been defined for it (`CreateMap<Address, AddressDto>()`), the mapper will automatically generate the call to `.ToAddressDto()`.

---

### 6. Messaging Kit

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

### 7. gRPC Kit (via `LinKit.Grpc`)

The gRPC Kit transforms your existing CQRS requests into fully functional, high-performance gRPC services with minimal effort. It completely automates the tedious process of writing gRPC service implementations, including request/response mapping, error handling, and CQRS mediator invocation.

-   **Zero Boilerplate:** No need to manually write gRPC service classes. Just decorate your CQRS requests.
-   **Automatic Mapping:** Intelligently maps properties between your Protobuf-generated classes and your C# CQRS request/response DTOs.
-   **Built-in Error Handling:** Automatically translates common exceptions (like `ValidationException`) into appropriate gRPC status codes.
-   **Seamless Integration:** Works perfectly with the local `IMediator` to execute your existing handlers.

#### How It Works

The gRPC Kit consists of two parts: a **Server Generator** and a **Client Generator**.

1.  **Server-Side:** You decorate a CQRS request class with the `[GrpcEndpoint]` attribute. You specify which gRPC service (from your `.proto` file) and method this request corresponds to. The source generator then creates a complete gRPC service class implementation (`LinKit...Service`) that:
    *   Inherits from your Protobuf-generated service base class (e.g., `UserServiceBase`).
    *   Overrides the specified method.
    *   Receives the gRPC request, maps it to your CQRS request.
    *   Calls the local `IMediator` to execute the corresponding handler.
    *   Maps the CQRS response back to the gRPC response.
    *   Handles exceptions gracefully.

2.  **Client-Side:** (See next section for details) You decorate the same CQRS request with `[GrpcClient]`. This generates an `IGrpcMediator` implementation that allows you to send the CQRS request from a client application, which then makes the gRPC call transparently.

---

#### Server-Side Usage

**Step 1: Define your `.proto` file**

First, define your gRPC service and messages as you normally would.

**Example `users.proto`:**
```protobuf
syntax = "proto3";

option csharp_namespace = "MyCompany.Grpc.Contracts";

package users;

service UserService {
  rpc GetUserById (GetUserByIdRequest) returns (GetUserByIdResponse);
  rpc CreateUser (CreateUserRequest) returns (CreateUserResponse);
}

message GetUserByIdRequest {
  int32 user_id = 1;
}

message UserDtoMessage {
    int32 id = 1;
    string user_name = 2;
    string email = 3;
}

message GetUserByIdResponse {
  UserDtoMessage user = 1;
}

message CreateUserRequest {
    string user_name = 1;
    string email = 2;
}

message CreateUserResponse {
    int32 new_user_id = 1;
}
```

**Step 2: Create Corresponding CQRS Requests and Handlers**

Create your CQRS queries, commands, and handlers as usual. Ensure the properties match the fields in your `.proto` messages for automatic mapping.

**The Query:**
```csharp
// Features/Users/GetUserQuery.cs
public class GetUserQuery : IQuery<UserDto> 
{
    // Property "UserId" will be mapped from "user_id" in the proto
    public int UserId { get; set; } 
}
```

**The Command:**
```csharp
// Features/Users/CreateUserCommand.cs
// This command returns the new user's ID
public class CreateUserCommand : ICommand<int>
{
    public string UserName { get; set; }
    public string Email { get; set; }
}
```

**The DTO:**
```csharp
// Dtos/UserDto.cs
public class UserDto
{
    public int Id { get; set; }
    public string UserName { get; set; }
    public string Email { get; set; }
}
```

*(You would also have `GetUserHandler` and `CreateUserHandler` marked with `[CqrsHandler]`)*

**Step 3: Decorate CQRS Requests with `[GrpcEndpoint]`**

This is the key step. Add the `[GrpcEndpoint]` attribute to your CQRS request classes to link them to the gRPC methods defined in your `.proto` file.

**Linking the Query:**
```csharp
using LinKit.Grpc;
using MyCompany.Grpc.Contracts; // Namespace from your .proto file

[GrpcEndpoint(typeof(UserService.UserServiceBase), "GetUserById")]
public class GetUserQuery : IQuery<UserDto> 
{
    public int UserId { get; set; } 
}
```
*   `typeof(UserService.UserServiceBase)`: The gRPC service base class generated by `protoc`.
*   `"GetUserById"`: The exact name of the RPC method in your service.

**Linking the Command:**
```csharp
using LinKit.Grpc;
using MyCompany.Grpc.Contracts;

[GrpcEndpoint(typeof(UserService.UserServiceBase), "CreateUser")]
public class CreateUserCommand : ICommand<int>
{
    public string UserName { get; set; }
    public string Email { get; set; }
}
```

**Step 4: Register and Map the gRPC Service**

In your server's `Program.cs`, register the necessary LinKit services and map the generated gRPC service.

```csharp
var builder = WebApplication.CreateBuilder(args);

// 1. Register standard LinKit CQRS and your handlers
builder.Services.AddLinKitCqrs();

// 2. Add gRPC core services
builder.Services.AddGrpc();

var app = builder.Build();

// 3. Map the source-generated gRPC service
//    The name is always "LinKit" + [YourServiceName]
app.MapGrpcService<LinKitUserService>();

app.Run();
```

**That's it!** You now have a fully functional gRPC service. When a client calls the `GetUserById` method, the generated `LinKitUserService` will:
1.  Instantiate a `GetUserQuery`.
2.  Map `GetUserByIdRequest.UserId` to `GetUserQuery.UserId`.
3.  Call `_mediator.QueryAsync(theQuery)`.
4.  Receive the `UserDto` from your `GetUserHandler`.
5.  Map the `UserDto` to a `GetUserByIdResponse`.
6.  Return the response to the client.

#### Automatic Mapping Conventions

LinKit automatically maps properties between your CQRS objects and Protobuf messages. It follows a simple, case-insensitive name matching rule. For Protobuf's `snake_case` convention, LinKit will correctly map `user_id` to a C# property named `UserId`.

This includes:
-   **Request Mapping:** `GrpcRequest` -> `CqrsRequest`
-   **Response Mapping:** `CqrsResponse` -> `GrpcResponse`
-   **Nested Lists:** It can even map collections, like a `List<ProductDto>` in C# to a `repeated ProductMessage` in Protobuf, as long as the item types have matching properties.

---

#### Client-Side Usage

*(This section can be updated with the new `IGrpcMediator` logic once you've finalized it)*

To call the gRPC service from another .NET application, you use the **Client-Side** generator.

**Step 1: Decorate the SAME CQRS Request with `[GrpcClient]`**

In your client project, reference the shared CQRS request classes and decorate them again, this time with `[GrpcClient]`.

```csharp
// In the Client Application project
using LinKit.Grpc;
using MyCompany.Grpc.Contracts;

[GrpcClient(typeof(UserService.UserClient), "GetUserByIdAsync")]
public class GetUserQuery : IQuery<UserDto> 
{
    public int UserId { get; set; } 
}
```
*   `typeof(UserService.UserClient)`: The gRPC **client** class generated by `protoc`.
*   `"GetUserByIdAsync"`: The name of the asynchronous client method.

**Step 2: Register the gRPC Client Kit**

In your client's `Program.cs`, configure the `IGrpcMediator` and provide a channel to the server.

```csharp
var builder = WebApplication.CreateBuilder(args);

// 1. Register the generated IGrpcMediator
builder.Services.AddLinKitGrpcClient();

// 2. Register a way to get the gRPC channel
//    This is a simple example. You can implement IGrpcChannelProvider
//    for more complex scenarios (e.g., service discovery).
builder.Services.AddSingleton<IGrpcChannelProvider>(
    new DefaultGrpcChannelProvider("https://localhost:7001") // Address of your gRPC server
);
```

**Step 3: Use `IGrpcMediator`**

Inject `IGrpcMediator` and use it just like the local `IMediator`. The API is identical.

```csharp
public class MyFrontendService
{
    private readonly IGrpcMediator _grpcMediator;

    public MyFrontendService(IGrpcMediator grpcMediator)
    {
        _grpcMediator = grpcMediator;
    }

    public async Task<UserDto> GetUserFromRemoteService(int id)
    {
        var query = new GetUserQuery { UserId = id };
        
        // This looks like a local call, but it's making a gRPC request!
        return await _grpcMediator.QueryAsync(query);
    }
}
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
