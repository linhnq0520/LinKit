using System;
using System.Collections.Generic;
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
            Validate(opts);
            services.AddSingleton<ICacheProvider>(sp => new HybridRedisCacheProvider(
                opts,
                sp.GetService<TimeProvider>(),
                sp.GetService<ILoggerFactory>()
            ));
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
        Validate(options);
        services.AddSingleton<ICacheProvider>(sp => new HybridRedisCacheProvider(
            options,
            sp.GetService<TimeProvider>(),
            sp.GetService<ILoggerFactory>()
        ));
        return services;
    }

    private static void Validate(LinKitRedisOptions options)
    {
        IReadOnlyList<string> errors = options.Validate();
        if (errors.Count == 0)
            return;

        throw new InvalidOperationException(
            "Redis caching is misconfigured: " + string.Join(" ", errors)
        );
    }
}
