using LinKit.Core.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace LinKit.Messaging.Kafka;

/// <summary>
/// Registration helpers for the Kafka transport adapters.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers the Kafka producer and consumer adapters, binding <see cref="KafkaOptions"/> from configuration.
    /// </summary>
    /// <param name="services">Service collection to configure.</param>
    /// <param name="configuration">Configuration root that contains the section.</param>
    /// <param name="sectionName">Section name to bind; defaults to <c>Kafka</c>.</param>
    /// <returns>The same collection, to allow call chaining.</returns>
    /// <exception cref="OptionsValidationException">The bound options are invalid; thrown on first resolution.</exception>
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
        // Validation runs on first IOptions<KafkaOptions>.Value resolve, which for a hosted consumer is startup.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<KafkaOptions>, KafkaOptionsValidator>()
        );

        // LinKit implementations. The connection is a singleton because it aggregates every topic the process subscribes
        // to and owns the shared consumer that feeds them.
        services.AddSingleton<IBrokerProducer, KafkaProducer>();
        services.AddSingleton<IBrokerConnection, KafkaConnection>();

        return services;
    }
}

/// <summary>
/// Turns <see cref="KafkaOptions.Validate"/> into the standard options validation pipeline, so a misconfigured
/// producer or consumer throws <c>OptionsValidationException</c> instead of failing later on the first message.
/// </summary>
internal sealed class KafkaOptionsValidator : IValidateOptions<KafkaOptions>
{
    public ValidateOptionsResult Validate(string? name, KafkaOptions options)
    {
        IReadOnlyList<string> errors = options.Validate();
        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
