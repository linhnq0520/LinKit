using System;
using Microsoft.Extensions.DependencyInjection;

namespace LinKit.Caching;

public static class CachingServiceCollectionExtensions
{
    public static IServiceCollection AddLinKitCaching(
        this IServiceCollection services,
        Action<LinKitRedisOptions>? configureRedis = null
    )
    {
        if (configureRedis is not null)
        {
            LinKitRedisOptions opts = new();
            configureRedis(opts);
            services.AddSingleton<ICacheProvider>(_ => new HybridRedisCacheProvider(opts));
            return services;
        }

        services.AddSingleton<ICacheProvider, InMemoryCacheProvider>();
        return services;
    }

    public static IServiceCollection AddLinKitRedisCaching(
        this IServiceCollection services,
        LinKitRedisOptions options
    )
    {
        services.AddSingleton<ICacheProvider>(_ => new HybridRedisCacheProvider(options));
        return services;
    }
}
