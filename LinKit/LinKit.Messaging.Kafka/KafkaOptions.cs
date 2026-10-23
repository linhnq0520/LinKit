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
}