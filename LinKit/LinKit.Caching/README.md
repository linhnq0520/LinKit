# LinKit.Caching

[![NuGet](https://img.shields.io/nuget/v/LinKit.Caching.svg)](https://www.nuget.org/packages/LinKit.Caching/)

`LinKit.Caching` provides `ICacheProvider` and a bounded in-memory implementation with absolute/sliding expiration and single-flight value creation. This package does not include Redis, Garnet, or Memcached adapters.

## Install

```shell
dotnet add package LinKit.Caching
```

## Register the in-memory provider

```csharp
builder.Services.AddLinKitCaching();
```

Inject `ICacheProvider` where needed:

```csharp
public sealed class ProductService(ICacheProvider cache, IProductRepository products)
{
    public ValueTask<Product?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        cache.GetOrCreateAsync(
            $"product:{id:N}",
            ct => products.FindByIdAsync(id, ct),
            ttl: TimeSpan.FromMinutes(5),
            cancellationToken);
}
```

`GetOrCreateAsync` shares an in-flight factory for the same key in `InMemoryCacheProvider`. A caller's cancellation cancels that caller's wait, not the shared factory; the factory is invoked with `CancellationToken.None`. Ensure factories have their own bounded I/O timeouts.

## Direct operations

```csharp
await cache.SetAsync(
    "catalog:featured",
    featuredProducts,
    ttl: TimeSpan.FromMinutes(10),
    sliding: TimeSpan.FromMinutes(2),
    cancellationToken);

var cached = await cache.GetAsync<List<Product>>("catalog:featured", cancellationToken);
await cache.RemoveAsync("catalog:featured", cancellationToken);
```

Use stable, namespaced keys and avoid putting secrets or raw personal data in cache keys. In-memory entries are process-local and are not shared between application instances. Capacity/LRU eviction is best-effort under concurrent writes; it is not a distributed cache or strict memory quota.

## Implement a distributed provider

Implement `ICacheProvider` and register the implementation after `AddLinKitCaching`, or register only your provider:

```csharp
builder.Services.AddSingleton<ICacheProvider, RedisCacheProvider>();
```

The interface includes `GetAsync`, `SetAsync`, `RemoveAsync`, and a default `GetOrCreateAsync`. The default method does not guarantee single-flight across concurrent callers; implement provider-specific coordination if that behavior is required. `ICacheKeyResolver<TRequest>` is a contract for a typed key resolver and must be implemented/registered by the application when used; this package does not currently generate cache-key resolvers.

`[CacheAside]` and `[InvalidateCache]` are metadata attributes only today; they do not add caching or invalidation behavior. The source generator reports `LKM003` when either attribute is used. Call `ICacheProvider.GetOrCreateAsync` and `RemoveAsync` explicitly until generated handling is available.

## Native AOT

The in-memory provider uses statically typed generic APIs and does not require runtime type discovery for its core operations. Validate the complete application, serializers, distributed provider, and dependencies with the target RID and AOT analyzers before claiming AOT compatibility.

## License

MIT. See the [repository license](https://github.com/linhnq0520/LinKit/blob/main/LICENSE).
