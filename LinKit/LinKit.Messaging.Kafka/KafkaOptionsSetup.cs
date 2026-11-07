using System.Globalization;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace LinKit.Messaging.Kafka;

/// <summary>
/// Binds <see cref="KafkaOptions"/> straight from an <see cref="IConfigurationSection"/>.
/// </summary>
/// <remarks>
/// The framework binder (<c>AddOptions&lt;T&gt;().Bind</c> / <c>Configure&lt;T&gt;(section)</c>) carries
/// <c>RequiresDynamicCode</c> and <c>RequiresUnreferencedCode</c> annotations, which makes an
/// <c>IsAotCompatible</c> package emit IL2026/IL3050 warnings. Binding the handful of scalar keys explicitly
/// keeps the package genuinely reflection-free, keeps number parsing culture-invariant, and keeps the default
/// value when a configured value is malformed instead of throwing at startup.
/// </remarks>
internal sealed class KafkaOptionsSetup : IConfigureOptions<KafkaOptions>
{
    private readonly IConfigurationSection _section;

    public KafkaOptionsSetup(IConfigurationSection section) => _section = section;

    public void Configure(KafkaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.BootstrapServers =
            _section[nameof(KafkaOptions.BootstrapServers)] ?? options.BootstrapServers;
        options.GroupId = _section[nameof(KafkaOptions.GroupId)] ?? options.GroupId;
        options.DeadLetterTopicSuffix =
            _section[nameof(KafkaOptions.DeadLetterTopicSuffix)] ?? options.DeadLetterTopicSuffix;

        if (
            ConfigurationValueBinding.TryReadInt32(
                _section,
                nameof(KafkaOptions.LingerMs),
                out var lingerMs
            )
        )
        {
            options.LingerMs = lingerMs;
        }

        if (
            ConfigurationValueBinding.TryReadInt32(
                _section,
                nameof(KafkaOptions.BatchSize),
                out var batchSize
            )
        )
        {
            options.BatchSize = batchSize;
        }

        if (
            ConfigurationValueBinding.TryReadInt32(
                _section,
                nameof(KafkaOptions.MaxProcessingAttempts),
                out var maxProcessingAttempts
            )
        )
        {
            options.MaxProcessingAttempts = maxProcessingAttempts;
        }

        if (
            ConfigurationValueBinding.TryReadEnum<CompressionType>(
                _section,
                nameof(KafkaOptions.CompressionType),
                out var compressionType
            )
        )
        {
            options.CompressionType = compressionType;
        }

        if (
            ConfigurationValueBinding.TryReadEnum<AutoOffsetReset>(
                _section,
                nameof(KafkaOptions.AutoOffsetReset),
                out var autoOffsetReset
            )
        )
        {
            options.AutoOffsetReset = autoOffsetReset;
        }

        if (
            ConfigurationValueBinding.TryReadBoolean(
                _section,
                nameof(KafkaOptions.EnableAutoCommit),
                out var enableAutoCommit
            )
        )
        {
            options.EnableAutoCommit = enableAutoCommit;
        }
    }
}

/// <summary>Culture-invariant, allocation-light scalar readers shared by the LinKit options binders.</summary>
internal static class ConfigurationValueBinding
{
    public static bool TryReadInt32(IConfigurationSection section, string key, out int value) =>
        int.TryParse(section[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    public static bool TryReadBoolean(IConfigurationSection section, string key, out bool value) =>
        bool.TryParse(section[key], out value);

    public static bool TryReadEnum<TEnum>(
        IConfigurationSection section,
        string key,
        out TEnum value
    )
        where TEnum : struct, Enum => Enum.TryParse(section[key], ignoreCase: true, out value);
}
