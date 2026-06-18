using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LinKit.Core.BackgroundJobs;

public static class BackgroundJobServiceCollectionExtensions
{
    public static IServiceCollection AddBackgroundJobManager(this IServiceCollection services)
    {
        if (
            services.Any(d =>
                d.ServiceType == typeof(BackgroundJobManager)
                || d.ImplementationType == typeof(BackgroundJobManager)
            )
        )
        {
            return services;
        }

        services.AddSingleton<BackgroundJobManager>();
        services.AddSingleton<IBackgroundJobTrigger>(sp =>
            sp.GetRequiredService<BackgroundJobManager>()
        );
        services.AddHostedService(sp => sp.GetRequiredService<BackgroundJobManager>());

        return services;
    }
}
