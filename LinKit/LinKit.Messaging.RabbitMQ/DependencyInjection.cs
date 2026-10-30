using LinKit.Core.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LinKit.Messaging.RabbitMQ;

public static class DependencyInjection
{
    public static IServiceCollection AddLinKitRabbitMQ(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = "RabbitMQ")
    {
        services.AddOptions<RabbitMqOptions>().Bind(configuration.GetSection(sectionName));
        services.TryAddSingleton<RabbitMqConnectionFactory>();
        services.TryAddSingleton<RabbitMqProducer>();
        services.TryAddSingleton<IBrokerProducer>(sp => sp.GetRequiredService<RabbitMqProducer>());
        services.TryAddSingleton<RabbitMqConnection>();
        services.TryAddSingleton<IBrokerConnection>(sp => sp.GetRequiredService<RabbitMqConnection>());
        return services;
    }

    public static IServiceCollection AddLinKitRabbitMQ(
        this IServiceCollection services,
        Action<RabbitMqOptions> configure)
    {
        services.Configure(configure);
        services.TryAddSingleton<RabbitMqConnectionFactory>();
        services.TryAddSingleton<RabbitMqProducer>();
        services.TryAddSingleton<IBrokerProducer>(sp => sp.GetRequiredService<RabbitMqProducer>());
        services.TryAddSingleton<RabbitMqConnection>();
        services.TryAddSingleton<IBrokerConnection>(sp => sp.GetRequiredService<RabbitMqConnection>());
        return services;
    }
}
