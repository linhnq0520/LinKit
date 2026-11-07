using LinKit.Core.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace LinKit.Messaging.RabbitMQ;

public static class DependencyInjection
{
    public static IServiceCollection AddLinKitRabbitMQ(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = "RabbitMQ"
    )
    {
        // AOT-safe equivalent of services.Configure<RabbitMqOptions>(section): explicit binder plus reload token.
        var section = configuration.GetSection(sectionName);
        services.AddOptions<RabbitMqOptions>();
        services.AddSingleton<IOptionsChangeTokenSource<RabbitMqOptions>>(
            new ConfigurationChangeTokenSource<RabbitMqOptions>(section)
        );
        services.AddSingleton<IConfigureOptions<RabbitMqOptions>>(
            new RabbitMqOptionsSetup(section)
        );
        services.TryAddSingleton<RabbitMqConnectionFactory>();
        services.TryAddSingleton<RabbitMqProducer>();
        services.TryAddSingleton<IBrokerProducer>(sp => sp.GetRequiredService<RabbitMqProducer>());
        services.TryAddSingleton<RabbitMqConnection>();
        services.TryAddSingleton<IBrokerConnection>(sp =>
            sp.GetRequiredService<RabbitMqConnection>()
        );
        return services;
    }

    public static IServiceCollection AddLinKitRabbitMQ(
        this IServiceCollection services,
        Action<RabbitMqOptions> configure
    )
    {
        services.Configure(configure);
        services.TryAddSingleton<RabbitMqConnectionFactory>();
        services.TryAddSingleton<RabbitMqProducer>();
        services.TryAddSingleton<IBrokerProducer>(sp => sp.GetRequiredService<RabbitMqProducer>());
        services.TryAddSingleton<RabbitMqConnection>();
        services.TryAddSingleton<IBrokerConnection>(sp =>
            sp.GetRequiredService<RabbitMqConnection>()
        );
        return services;
    }
}
