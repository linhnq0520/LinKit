using LinKit.Core.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LinKit.Messaging.RabbitMQ;

public static class DependencyInjection
{
    public static IServiceCollection AddLinKitRabbitMQ(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = "RabbitMQ")
    {
        services.AddOptions<RabbitMqOptions>().Bind(configuration.GetSection(sectionName));
        services.AddSingleton<RabbitMqConnectionFactory>();
        services.AddSingleton<IBrokerProducer, RabbitMqProducer>();
        services.AddSingleton<IBrokerConnection, RabbitMqConnection>();
        return services;
    }
}