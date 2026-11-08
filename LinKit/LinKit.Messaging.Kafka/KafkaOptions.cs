using Confluent.Kafka;

namespace LinKit.Messaging.Kafka;

public class KafkaOptions
{
    public string BootstrapServers { get; set; } = "localhost:9092";
    public string GroupId { get; set; } = "linkit-consumer-group";
    public int LingerMs { get; set; } = 5;
    public int BatchSize { get; set; } = 64 * 1024;
    public CompressionType CompressionType { get; set; } = CompressionType.None;
    public AutoOffsetReset AutoOffsetReset { get; set; } = AutoOffsetReset.Earliest;
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
