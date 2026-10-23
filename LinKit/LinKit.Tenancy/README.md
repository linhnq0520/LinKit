# LinKit.Tenancy

[![NuGet](https://img.shields.io/nuget/v/LinKit.Tenancy.svg)](https://www.nuget.org/packages/LinKit.Tenancy/)

`LinKit.Tenancy` provides an asynchronous-flow tenant context and helpers to explicitly place/read a tenant ID in LinKit message headers. It does not automatically resolve HTTP identity, authorize a tenant, or filter database queries.

## Install and register

```shell
dotnet add package LinKit.Tenancy
```

```csharp
builder.Services.AddLinKitTenancy();
```

Inject `ITenantContext` into scoped application services:

```csharp
public sealed class TenantOrderService(ITenantContext tenant, IOrderRepository orders)
{
    public Task<IReadOnlyList<Order>> GetOrdersAsync(CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId
            ?? throw new InvalidOperationException("A tenant must be resolved before querying orders.");
        return orders.GetForTenantAsync(tenantId, cancellationToken);
    }
}
```

The application must validate the tenant ID and enforce isolation in authorization and data-access layers. Merely setting `TenantId` does not add a database filter.

## Resolve and clear tenant context

The package does not install HTTP middleware or extract tenant headers for you. Resolve the tenant in application middleware, set the current context, and clear/restore it at the request or message-processing boundary:

```csharp
var previous = tenantAccessor.Context;
try
{
    tenantAccessor.Context = new TenantContext { TenantId = validatedTenantId };
    await next();
}
finally
{
    tenantAccessor.Context = previous;
}
```

`TenantAccessor` uses `AsyncLocal` so context follows the asynchronous execution flow. Avoid passing tenant-bound work into detached/background tasks without explicitly copying the intended tenant context.

## Propagate through message headers

```csharp
var headers = new MessageHeaders();
TenantPropagation.Inject(headers, tenantContext.TenantId);

var incomingTenantId = TenantPropagation.Extract(headers);
if (incomingTenantId is not null)
{
    tenantAccessor.Context.TenantId = ValidateTenantId(incomingTenantId);
}
```

Generated consumers call every registered `IMessageConsumeInterceptor` per message. `AddLinKitTenancy()` registers `TenantConsumeInterceptor`, which sets the tenant from `linkit-tenant-id` for that message and restores the previous context afterwards. The header is trusted broker metadata, so only allow trusted publishers on the queue. Tenant data filtering still must be enforced by your data layer.

The message header key is `linkit-tenant-id`. `TenantHeaderName` (`x-tenant-id`) is a separate HTTP naming constant; the message helper does not automatically read/write HTTP headers or broker metadata.

## License

MIT. See the [repository license](https://github.com/linhnq0520/LinKit/blob/main/LICENSE).
