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
        // Validation runs on first IOptions<RabbitMqOptions>.Value resolve, which for the hosted consumer is startup.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IValidateOptions<RabbitMqOptions>,
                RabbitMqOptionsValidator
            >()
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
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IValidateOptions<RabbitMqOptions>,
                RabbitMqOptionsValidator
            >()
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
}

/// <summary>
/// Turns <see cref="RabbitMqOptions.Validate"/> into the standard options validation pipeline, so a misconfigured
/// broker or exchange type throws <c>OptionsValidationException</c> instead of failing on the first publish.
/// </summary>
internal sealed class RabbitMqOptionsValidator : IValidateOptions<RabbitMqOptions>
{
    public ValidateOptionsResult Validate(string? name, RabbitMqOptions options)
    {
        IReadOnlyList<string> errors = options.Validate();
        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
