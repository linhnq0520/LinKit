using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LinKit.Caching;

/// <summary>
/// Registration helpers for <see cref="ICacheProvider"/>.
/// </summary>
public static class CachingServiceCollectionExtensions
{
    /// <summary>
    /// Registers a cache provider. Without <paramref name="configureRedis"/> the process-local
    /// <see cref="InMemoryCacheProvider"/> is used; with it, a <see cref="HybridRedisCacheProvider"/> that fronts Redis
    /// with an in-memory L1 is registered instead.
    /// </summary>
    /// <param name="services">Service collection to configure.</param>
    /// <param name="configureRedis">Redis settings; omit to stay in-process.</param>
    /// <returns>The same collection, to allow call chaining.</returns>
    /// <exception cref="InvalidOperationException">The configured Redis options are invalid.</exception>
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

    /// <summary>Registers a <see cref="HybridRedisCacheProvider"/> from an explicit options instance.</summary>
    /// <param name="services">Service collection to configure.</param>
    /// <param name="options">Redis settings, validated before registration.</param>
    /// <returns>The same collection, to allow call chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The supplied options are invalid.</exception>
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
