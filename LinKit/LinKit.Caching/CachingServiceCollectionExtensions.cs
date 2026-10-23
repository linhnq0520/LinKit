using Microsoft.Extensions.DependencyInjection;

namespace LinKit.Caching;

public static class CachingServiceCollectionExtensions
{
    public static IServiceCollection AddLinKitCaching(this IServiceCollection services)
    {
        services.AddSingleton<ICacheProvider, InMemoryCacheProvider>();
        return services;
    }
}
