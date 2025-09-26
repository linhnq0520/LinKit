# LinKit.Grpc

**LinKit.Grpc** extends **LinKit.Core** to provide **automatic gRPC server and client code generation** based on your CQRS requests and handlers.
The goal is to remove boilerplate, eliminate manual mapping, and let you focus on business logic.

---

## 🚀 Features

### gRPC Server

* Generate gRPC service implementations from CQRS requests marked with `[GrpcEndpoint]`.
* Automatic request/response → gRPC message mapping.
* Seamless integration with LinKit mediator.

### gRPC Client

* Generate mediator-friendly gRPC client calls from CQRS requests marked with `[GrpcClient]`.
* Built-in support for interceptors (logging, retry, authentication, …).
* Automatic `Metadata` (headers) injection.

### Mapping

* Automatic property mapping by name.
* Supports `[JsonPropertyName]` and LinKit.Core mapping configuration.

### Runtime

* **NativeAOT & Trimming Ready**.

---

## 📦 Installation

```sh
dotnet add package LinKit.Grpc
```

---

## ⚡ Quick Start

### 1. Server

**CQRS request:**

```csharp
[GrpcEndpoint(typeof(UserServiceBase), "GetUser")]
public class GetUserQuery : IQuery<UserDto>
{
    public int Id { get; set; }
}
```

**Handler:**

```csharp
[CqrsHandler]
public class GetUserHandler : IQueryHandler<GetUserQuery, UserDto>
{
    public Task<UserDto> HandleAsync(GetUserQuery query, CancellationToken ct)
        => Task.FromResult(new UserDto { Id = query.Id, Name = "Demo" });
}
```

**Register gRPC server:**

```csharp
builder.Services.AddLinKitGrpcServer();
app.MapGrpcService<LinKitUserService>();
```

---

### 2. Client

**CQRS request:**

```csharp
[GrpcClient(typeof(UserServiceClient), "GetUserAsync")]
public class GetUserQuery : IQuery<UserDto>
{
    public int Id { get; set; }
}
```

**Register gRPC client:**

```csharp
builder.Services.AddLinKitGrpcClient();

// --- Required ---
// Option 1: simple URL configuration
builder.Services.AddGrpcChannel("https://localhost:5001");

// Option 2: custom provider (multiple URLs, SSL, advanced setup)
builder.Services.AddGrpcChannelProvider<MyChannelProvider>();

// --- Optional ---
// Add interceptors (logging, retry, auth, …)
builder.Services.AddGrpcInterceptorProvider<MyInterceptorProvider>();

// Add metadata (headers) provider
builder.Services.AddGrpcMetadataProvider<MyMetadataProvider>();
```

**Send via mediator:**

```csharp
var user = await mediator.QueryAsync(new GetUserQuery { Id = 1 });
```

---

### 3. Channel Provider Example

```csharp
public class MyChannelProvider : IGrpcChannelProvider
{
    private readonly Dictionary<Type, string> _serviceUrls = new()
    {
        { typeof(UserService.UserServiceClient), "https://localhost:5001" },
        { typeof(OrderService.OrderServiceClient), "https://orders.myapi.com" }
    };

    public GrpcChannel GetChannel(Type clientType)
    {
        // Select endpoint URL by client type
        if (!_serviceUrls.TryGetValue(clientType, out var url))
        {
            throw new InvalidOperationException(
                $"No gRPC endpoint configured for client type {clientType.FullName}");
        }

        // Configure HttpClientHandler (SSL, certificates, etc.)
        var httpHandler = new HttpClientHandler
        {
            // Example: allow self-signed certs in dev
            ServerCertificateCustomValidationCallback = 
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        // Configure channel options
        var channelOptions = new GrpcChannelOptions
        {
            HttpHandler = httpHandler,
            MaxReceiveMessageSize = 2 * 1024 * 1024, // 2 MB
            MaxSendMessageSize = 2 * 1024 * 1024
        };

        return GrpcChannel.ForAddress(url, channelOptions);
    }
}
```

### 4. Interceptor Example

```csharp
public class MyInterceptorProvider : IGrpcInterceptorProvider
{
    public Interceptor[] GetInterceptors(Type clientType)
        => new Interceptor[] { new LoggingInterceptor() };
}
```

---

## 🔧 Configuration

* `[GrpcEndpoint]` → expose a CQRS request as a gRPC method.
* `[GrpcClient]` → generate gRPC client code for a CQRS request.
* **Mapping** is automatic but can be customized with LinKit.Core mapping configuration.
* Interceptors + metadata providers let you implement cross-cutting concerns (auth, logging, retry).

---

## ✅ Notes

* **Channels** are provided by `IGrpcChannelProvider`.

  * Do **not** call `GrpcChannel.ForAddress` directly on every request.
  * The factory ensures channel reuse for performance.
* **Intercept** is applied on a **CallInvoker**, not directly on `GrpcChannel`.
  Generated code uses:

  ```csharp
  var callInvoker = channel.Intercept(interceptors);
  var client = new UserServiceClient(callInvoker);
  ```
* Separation of **channel, interceptor, and metadata** providers makes it easy to swap or extend.

---