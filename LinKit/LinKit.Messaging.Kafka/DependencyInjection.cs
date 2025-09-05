using LinKit.Core.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LinKit.Messaging.Kafka;

public static class DependencyInjection
{
    public static IServiceCollection AddLinKitKafka(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = "Kafka")
    {
        // Đăng ký options
        services.AddOptions<KafkaOptions>()
            .Bind(configuration.GetSection(sectionName));

        // Đăng ký các triển khai của LinKit
        services.AddSingleton<IBrokerProducer, KafkaProducer>();
        // IBrokerConnection được quản lý như một singleton để nó có thể thu thập tất cả các topic cần subscribe
        services.AddSingleton<IBrokerConnection, KafkaConnection>();

        return services;
    }
}