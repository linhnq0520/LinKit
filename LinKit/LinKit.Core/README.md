# LinKit.Core

[![NuGet Version](https://img.shields.io/nuget/v/LinKit.Core.svg)](https://www.nuget.org/packages/LinKit.Core/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/LinKit.Core.svg)](https://www.nuget.org/packages/LinKit.Core/)

**LinKit.Core** is a high-performance, modular toolkit for .NET, providing source-generated helpers for CQRS, Dependency Injection, Minimal API Endpoints, Background Jobs, Mapping, Messaging, and gRPC. LinKit eliminates boilerplate, maximizes runtime performance, and is fully compatible with NativeAOT and trimming.

---

## Why LinKit?

Most .NET libraries rely on runtime reflection, which is slow, memory-intensive, and incompatible with NativeAOT. LinKit uses C# Source Generators to analyze your code and generate optimized, boilerplate-free C# at compile time, linking your application's components together.

**Key Benefits:**

- 🚀 **Zero Reflection:** No runtime scanning or reflection.
- ⚡ **Fast Startup:** No assembly scanning.
- 🗑️ **AOT & Trimming Safe:** Works with Blazor, MAUI, NativeAOT.
- ✍️ **Clean API:** Intent-driven, explicit, and easy to use.
- 🤖 **Automated Boilerplate:** For DI, API endpoints, background jobs, gRPC, messaging, and mapping.

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

````shell
dotnet add package LinKit.Core
```Add other packages as needed:
```shell
dotnet add package LinKit.Grpc
dotnet add package LinKit.Messaging.RabbitMQ
````

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
    public Task<UserDto> Handle(GetUserQuery query, CancellationToken cancellationToken) { ... }
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

### 4. Background Job Kit

The Background Job Kit provides a powerful, configuration-driven system for executing CQRS commands and queries on a schedule. It source-generates the necessary infrastructure to link your job definitions to your CQRS handlers, creating a robust background processing system with zero reflection and full support for hot-reloading configurations.

#### How It Works

1.  The `[BackgroundJob]` attribute tells the source generator to map a unique, human-readable name to a specific CQRS request type.
2.  At runtime, the registered `BackgroundJobManager` (an `IHostedService`) reads your JSON configuration.
3.  It uses the generated map to find the correct CQRS request for each configured job and uses the Mediator to execute it according to the specified schedule. The entire process is type-safe and performant.

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

    public Task Handle(ProcessEndOfDayReportCommand command, CancellationToken cancellationToken)
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
        "RunOnStart": true,
        "ScheduleType": "Daily",
        "TimeOfDay": "08:00:00"
      },
      {
        "Name": "WeeklyDatabaseCleanup",
        "IsActive": true,
        "ScheduleType": "Weekly",
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

// Register the Background Job hosted service
builder.AddBackgroundJobs(<path to config file>));

var app = builder.Build();

// ...
```

This registers the `BackgroundJobManager` which will automatically start, stop, and reload jobs based on your configuration.

---

#### Configuration Details

Below is a detailed description of each property available in the job configuration.

| Property              | Type              | Description                                                                                                                                                                                            |
| --------------------- | ----------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `Name`                | `string`          | **Required.** The unique identifier for the job. This must exactly match the name provided in the `[BackgroundJob("MyUniqueJobName")]` attribute.                                                       |
| `IsActive`            | `bool`            | Determines if the job is enabled. If set to `false`, the job will not run. Changes are detected at runtime.                                                                                              |
| `RunOnStart`          | `bool`            | If `true`, the job will execute once immediately upon application start (or when the job is activated via config change), and then follow its regular schedule. Defaults to `false`.                      |
| `ScheduleType`        | `string`          | **Required.** The scheduling mode. Can be one of four values: `Interval`, `Daily`, `Weekly`, `Monthly`.                                                                                                  |
| `TimeIntervalSeconds` | `int`             | Used only when `ScheduleType` is `Interval`. Defines the number of seconds to wait between each job execution.                                                                                         |
| `TimeOfDay`           | `string`          | Used for `Daily`, `Weekly`, and `Monthly` schedules. Defines the time of day (in **UTC**) to run the job. Format: `"HH:mm:ss"`. Example: `"14:30:00"` for 2:30 PM UTC.                                    |
| `DayOfWeek`           | `string`          | Used only when `ScheduleType` is `Weekly`. The day of the week to run the job. Examples: `"Monday"`, `"Tuesday"`, etc.                                                                                  |
| `DayOfMonth`          | `int`             | Used only when `ScheduleType` is `Monthly`. The day of the month to run (1-31). **Special Value:** Use a large number (e.g., `99`) to signify the **last day** of the current month.                   |
| `MaxParallel`         | `int`             | The maximum number of instances of this job that can run concurrently. Defaults to `1`. Useful for long-running jobs to prevent overlap.                                                              |
| `EmbeddedData`        | `string` (JSON)   | An optional string value that is passed directly to the EmbeddedData property of your command. This can be a simple string, a JSON object, or any other format you wish to parse in your handler.      |

### 5. Mapping Kit

A high-performance, reflection-free, source-generated object mapper. The Mapping Kit provides a fluent and type-safe API to configure mappings, which are then transformed into highly optimized, direct-assignment code at compile time.

- **Type-Safe API:** Uses lambda expressions (`dest => dest.Property`) to eliminate magic strings and catch errors at compile time.
- **Fluent Configuration:** Chain `.ForMember()` calls to create readable and maintainable mapping rules.
- **Convention-Based:** Automatically maps properties with matching names or `[JsonPropertyName]` attributes.
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

````csharp
builder.Services.AddLinKitCqrs();
builder.Services.AddLinKitMessaging();
builder.Services.AddLinKitRabbitMQ(configuration);```

---

### 7. gRPC Kit (via LinKit.Grpc)

Source-generates gRPC server and client code for CQRS requests.

**Server:**
- `[GrpcEndpoint(typeof(MyServiceBase), "MethodName")]` on CQRS request.
- Handler: `[CqrsHandler]`
- Register: `builder.Services.AddLinKitGrpcServer();` and `app.MapGrpcService<LinKitMyService>();`

```csharp
[GrpcEndpoint(typeof(UserService.UserServiceBase), "GetUserById")]
public class GetUserQuery : IQuery<UserDto> { ... }
````

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
