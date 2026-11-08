# LinKit.Caching

[![NuGet](https://img.shields.io/nuget/v/LinKit.Caching.svg)](https://www.nuget.org/packages/LinKit.Caching/)

`LinKit.Caching` provides `ICacheProvider` with a bounded in-memory implementation and a high-performance, 100% Native AOT-compatible two-level (L1 In-Memory + L2 Redis) hybrid cache provider using a lightweight RESP2 socket client.

## Install

```shell
dotnet add package LinKit.Caching
```

## Register Cache Providers

### In-Memory Provider

```csharp
builder.Services.AddLinKitCaching();
```

### Hybrid (L1 In-Memory + L2 Redis) Provider

Configure Redis connection directly:

```csharp
builder.Services.AddLinKitCaching(options =>
{
    options.Enabled = true;
    options.Host = "localhost";
    options.Port = 6379;
    options.DefaultTtl = TimeSpan.FromMinutes(10);
    options.ReadTimeout = TimeSpan.FromSeconds(5); // default; prevents hung-Redis stalls
});
```

Or using explicit configuration:

```csharp
builder.Services.AddLinKitRedisCaching(new LinKitRedisOptions
{
    Host = "localhost",
    Port = 6379,
    DefaultTtl = TimeSpan.FromMinutes(10),
    ReadTimeout = TimeSpan.FromSeconds(5)
});
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

### Hybrid cache semantics

- Cached default values (`0`, `false`, `Guid.Empty`, and `null`) are cache **hits** in both L1 and L2 — lookups use found-based semantics, not default-value checks.
- L1 backfill never outlives the L2 TTL: values carry an absolute-expiry envelope.
- `SetAsync`/`RemoveAsync` invalidate in-flight factory results, so stale values cannot overwrite fresh ones.
- The provider accepts optional `TimeProvider` and `ILoggerFactory` constructor parameters.

## Redis client behavior

- The client validates `AUTH`/`SELECT` replies — bad credentials now throw `RedisServerException` at connection time instead of silently returning null forever. Protocol `-ERR` replies throw.
- Transient network errors and timeouts are logged (Warning) and treated as cache misses.
- `ReadTimeout` (default 5s) prevents hung-Redis stalls; an optional `ILogger` can be injected.
- Buffered RESP reading (`ArrayPool`) replaces byte-at-a-time reads.

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

`[CacheAside]` and `[InvalidateCache]` were **removed** in LinKit.Core 4.0.0. They only carried metadata and never cached or invalidated anything, so they were dropped instead of kept as an API that promises behavior it cannot deliver. Call `ICacheProvider.GetOrCreateAsync` and `RemoveAsync`/`RemoveByPrefixAsync` explicitly.

`AddLinKitCaching` and `AddLinKitRedisCaching` validate the options before the provider is registered. A bad `Host`, `Port`, `Database`, `DefaultTtl`, `ConnectTimeout` or `ReadTimeout` throws `InvalidOperationException` at startup instead of turning every read into a silent miss.

## Native AOT

The in-memory provider uses statically typed generic APIs and does not require runtime type discovery for its core operations. Validate the complete application, serializers, distributed provider, and dependencies with the target RID and AOT analyzers before claiming AOT compatibility.

## License

MIT. See the [repository license](https://github.com/linhnq0520/LinKit/blob/main/LICENSE).

