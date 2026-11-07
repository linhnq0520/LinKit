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

Inject `ITenantAccessor` (or `ITenantContext`) into scoped application services:

```csharp
public sealed class TenantOrderService(ITenantAccessor tenants, IOrderRepository orders)
{
    public Task<IReadOnlyList<Order>> GetOrdersAsync(CancellationToken cancellationToken)
    {
        // Require throws when no scope is active; TryGet is the opt-out form when a tenant is optional.
        var tenantId = tenants.Require().TenantId
            ?? throw new InvalidOperationException("A tenant must be resolved before querying orders.");
        return orders.GetForTenantAsync(tenantId, cancellationToken);
    }
}
```

The application must validate the tenant ID and enforce isolation in authorization and data-access layers. Merely setting `TenantId` does not add a database filter.

## Resolve and clear tenant context

Nothing is created implicitly: outside a scope `ITenantAccessor.Current` is `null`, `TryGet` returns `false`, and `Require()` throws. Resolve the tenant at the request or message-processing boundary and wrap the work in a scope — disposing it restores whatever tenant was ambient before (usually `null`):

```csharp
using (tenantAccessor.BeginScope(validatedTenantId))
{
    await next();
}

Assert.Null(tenantAccessor.Current); // guaranteed reset, even when `next()` throws
```

Scopes nest: an inner scope restores the outer tenant when it is disposed. `TenantAccessor` uses `AsyncLocal` so the tenant follows the asynchronous execution flow. Avoid passing tenant-bound work into detached/background tasks without explicitly starting a scope there.

For outbound calls, `TenantPropagationHandler` is a `DelegatingHandler` that adds the `x-tenant-id` header and re-establishes the ambient tenant for the duration of the request:

```csharp
services.AddHttpClient<InventoryClient>(client => client.BaseAddress = inventoryUri)
        .AddHttpMessageHandler<TenantPropagationHandler>();
```

## Propagate through message headers

```csharp
var headers = new MessageHeaders();
TenantPropagation.Inject(headers, tenantAccessor.Current?.TenantId);

var incomingTenantId = TenantPropagation.Extract(headers);
```

Generated consumers call every registered `IMessageConsumeInterceptor` per message. `AddLinKitTenancy()` registers `TenantConsumeInterceptor`, which sets the tenant from `linkit-tenant-id` for that message and restores the previous context afterwards. The header is trusted broker metadata, so only allow trusted publishers on the queue. Tenant data filtering still must be enforced by your data layer.

`OutboxMessage.Create(...)` captures the ambient tenant into the same header, and `OutboxPublisherService` restores it before handing the message to the broker, so the tenant survives the transaction boundary without any wiring.

The message header key is `linkit-tenant-id` (`MessageHeaderNames.TenantId`). `TenantHeaderName` (`x-tenant-id`) is the separate HTTP naming constant.

## License

MIT. See the [repository license](https://github.com/linhnq0520/LinKit/blob/main/LICENSE).
