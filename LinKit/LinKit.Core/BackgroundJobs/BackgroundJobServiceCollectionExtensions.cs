using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LinKit.Core.BackgroundJobs;

/// <summary>
/// Registration helpers for the background job subsystem.
/// </summary>
public static class BackgroundJobServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="BackgroundJobManager"/> once as a singleton, exposes it through
    /// <see cref="IBackgroundJobTrigger"/> and starts it with the host. The call is idempotent, so several modules may
    /// request it without creating duplicate schedulers.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The same collection, so calls can be chained.</returns>
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
