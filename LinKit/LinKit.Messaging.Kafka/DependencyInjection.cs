using LinKit.Core.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LinKit.Messaging.Kafka;

public static class DependencyInjection
{
    public static IServiceCollection AddLinKitKafka(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = "Kafka"
    )
    {
        // AOT-safe equivalent of services.Configure<KafkaOptions>(section): explicit binder plus reload token.
        var section = configuration.GetSection(sectionName);
        services.AddOptions<KafkaOptions>();
        services.AddSingleton<IOptionsChangeTokenSource<KafkaOptions>>(
            new ConfigurationChangeTokenSource<KafkaOptions>(section)
        );
        services.AddSingleton<IConfigureOptions<KafkaOptions>>(new KafkaOptionsSetup(section));

        // Đăng ký các triển khai của LinKit
        services.AddSingleton<IBrokerProducer, KafkaProducer>();
        // IBrokerConnection được quản lý như một singleton để nó có thể thu thập tất cả các topic cần subscribe
        services.AddSingleton<IBrokerConnection, KafkaConnection>();

        return services;
    }
}
