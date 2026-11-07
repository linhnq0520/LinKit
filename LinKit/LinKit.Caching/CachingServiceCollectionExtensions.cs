using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
            services.AddSingleton<ICacheProvider>(sp => new HybridRedisCacheProvider(
                opts,
                sp.GetService<TimeProvider>(),
                sp.GetService<ILoggerFactory>()));
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
        services.AddSingleton<ICacheProvider>(sp => new HybridRedisCacheProvider(
            options,
            sp.GetService<TimeProvider>(),
            sp.GetService<ILoggerFactory>()));
        return services;
    }
}
