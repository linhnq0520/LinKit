using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace LinKit.Messaging.RabbitMQ;

/// <summary>
/// Binds <see cref="RabbitMqOptions"/> straight from an <see cref="IConfigurationSection"/>.
/// </summary>
/// <remarks>
/// The framework binder (<c>AddOptions&lt;T&gt;().Bind</c> / <c>Configure&lt;T&gt;(section)</c>) carries
/// <c>RequiresDynamicCode</c> and <c>RequiresUnreferencedCode</c> annotations, which makes an
/// <c>IsAotCompatible</c> package emit IL2026/IL3050 warnings. Binding the handful of scalar keys explicitly
/// keeps the package genuinely reflection-free and keeps the default value when a configured value is malformed
/// instead of throwing at startup.
/// </remarks>
internal sealed class RabbitMqOptionsSetup : IConfigureOptions<RabbitMqOptions>
{
    private readonly IConfigurationSection _section;

    public RabbitMqOptionsSetup(IConfigurationSection section) => _section = section;

    public void Configure(RabbitMqOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.HostName = _section[nameof(RabbitMqOptions.HostName)] ?? options.HostName;
        options.UserName = _section[nameof(RabbitMqOptions.UserName)] ?? options.UserName;
        options.Password = _section[nameof(RabbitMqOptions.Password)] ?? options.Password;
        options.VirtualHost = _section[nameof(RabbitMqOptions.VirtualHost)] ?? options.VirtualHost;
        options.DeadLetterExchange =
            _section[nameof(RabbitMqOptions.DeadLetterExchange)] ?? options.DeadLetterExchange;
        options.DefaultExchangeType =
            _section[nameof(RabbitMqOptions.DefaultExchangeType)] ?? options.DefaultExchangeType;

        if (
            ConfigurationValueBinding.TryReadInt32(
                _section,
                nameof(RabbitMqOptions.Port),
                out var port
            )
        )
        {
            options.Port = port;
        }

        if (
            ConfigurationValueBinding.TryReadInt32(
                _section,
                nameof(RabbitMqOptions.ChannelPoolSize),
                out var poolSize
            )
        )
        {
            options.ChannelPoolSize = poolSize;
        }

        if (
            ConfigurationValueBinding.TryReadInt32(
                _section,
                nameof(RabbitMqOptions.MaxRetryAttempts),
                out var retries
            )
        )
        {
            options.MaxRetryAttempts = retries;
        }

        if (
            ConfigurationValueBinding.TryReadUInt16(
                _section,
                nameof(RabbitMqOptions.PrefetchCount),
                out var prefetch
            )
        )
        {
            options.PrefetchCount = prefetch;
        }

        if (
            ConfigurationValueBinding.TryReadBoolean(
                _section,
                nameof(RabbitMqOptions.RequeueOnFailure),
                out var requeue
            )
        )
        {
            options.RequeueOnFailure = requeue;
        }

        if (
            ConfigurationValueBinding.TryReadBoolean(
                _section,
                nameof(RabbitMqOptions.PublisherConfirms),
                out var confirms
            )
        )
        {
            options.PublisherConfirms = confirms;
        }
    }
}

/// <summary>Culture-invariant, allocation-light scalar readers used by the LinKit.RabbitMQ options binder.</summary>
internal static class ConfigurationValueBinding
{
    public static bool TryReadInt32(IConfigurationSection section, string key, out int value) =>
        int.TryParse(section[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    public static bool TryReadUInt16(IConfigurationSection section, string key, out ushort value) =>
        ushort.TryParse(
            section[key],
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out value
        );

    public static bool TryReadBoolean(IConfigurationSection section, string key, out bool value) =>
        bool.TryParse(section[key], out value);
}
