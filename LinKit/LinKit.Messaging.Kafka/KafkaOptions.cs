using Confluent.Kafka;

namespace LinKit.Messaging.Kafka;

/// <summary>
/// Kafka producer and consumer settings used by the LinKit Kafka transport adapters.
/// </summary>
/// <remarks>
/// Values are validated by <c>AddLinKitKafka</c> through <c>IValidateOptions&lt;KafkaOptions&gt;</c>, so a bad
/// configuration fails on first resolution instead of degrading silently.
/// </remarks>
public class KafkaOptions
{
    /// <summary>Comma separated <c>host:port</c> list of brokers used to bootstrap the cluster metadata.</summary>
    public string BootstrapServers { get; set; } = "localhost:9092";

    /// <summary>Consumer group that owns the committed offsets; each group reads the topic independently.</summary>
    public string GroupId { get; set; } = "linkit-consumer-group";

    /// <summary>Time to wait for more records before a batch is sent; higher values trade latency for throughput.</summary>
    public int LingerMs { get; set; } = 5;

    /// <summary>Maximum bytes batched per partition before a send is forced.</summary>
    public int BatchSize { get; set; } = 64 * 1024;

    /// <summary>Compression codec applied to produced batches.</summary>
    public CompressionType CompressionType { get; set; } = CompressionType.None;

    /// <summary>Where to start reading when the group has no committed offset.</summary>
    public AutoOffsetReset AutoOffsetReset { get; set; } = AutoOffsetReset.Earliest;

    /// <summary>
    /// Whether offsets are committed automatically. The default <see langword="false"/> commits after the handler succeeds
    /// so a crash replays the record instead of losing it.
    /// </summary>
    public bool EnableAutoCommit { get; set; } = false;

    /// <summary>Handler attempts per record before it is dead-lettered. 0 = retry forever (can block the partition).</summary>
    public int MaxProcessingAttempts { get; set; } = 5;

    /// <summary>Appended to the source topic name to build the dead-letter topic.</summary>
    public string DeadLetterTopicSuffix { get; set; } = ".dlq";

    /// <summary>
    /// Collects configuration problems; the list is empty when the options are usable. The registered
    /// <c>IValidateOptions&lt;KafkaOptions&gt;</c> runs this on first resolve so a bad configuration fails visibly
    /// instead of degrading silently (an empty group id, for example, re-reads the whole topic on every restart).
    /// </summary>
    internal IReadOnlyList<string> Validate()
    {
        List<string> errors = [];

        if (string.IsNullOrWhiteSpace(BootstrapServers))
            errors.Add("BootstrapServers must be a comma separated host:port list.");
        if (string.IsNullOrWhiteSpace(GroupId))
            errors.Add(
                "GroupId must not be empty; the consumer group identifies committed offsets."
            );
        if (BatchSize <= 0)
            errors.Add("BatchSize must be greater than zero.");
        if (LingerMs < 0)
            errors.Add("LingerMs must not be negative.");
        if (MaxProcessingAttempts < 0)
            errors.Add("MaxProcessingAttempts must be zero (retry forever) or positive.");
        if (DeadLetterTopicSuffix is null)
            errors.Add(
                "DeadLetterTopicSuffix must not be null; use an empty string to disable the suffix."
            );

        return errors;
    }
}
