namespace LinKit.Core.Messaging;

/// <summary>
/// Declares the broker destination of a message type and its delivery policy, so producers and generated consumers agree
/// on the topology without configuration. The generated code reads this attribute when wiring publish and subscribe.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class MessageAttribute : Attribute
{
    /// <summary>
    /// Gets the topic name for Kafka or the exchange name for RabbitMQ.
    /// </summary>
    public string TopicOrExchange { get; }

    /// <summary>
    /// Gets or sets the binding routing key used by routing-capable brokers. When <see langword="null"/>, the topic or
    /// exchange name is used.
    /// </summary>
    public string? RoutingKey { get; set; }

    /// <summary>
    /// Gets or sets the queue the consumer creates and reads from. When <see langword="null"/>, a queue name is derived
    /// from the message type.
    /// </summary>
    public string? QueueName { get; set; }

    /// <summary>
    /// Gets or sets how many times a failed delivery is retried before it is dead-lettered. Defaults to 0, which
    /// dead-letters on the first failure.
    /// </summary>
    public int MaxRetries { get; set; } = 0;

    /// <summary>
    /// Gets or sets the base delay between retries in milliseconds. Defaults to 1000.
    /// </summary>
    public int RetryIntervalMs { get; set; } = 1000;

    /// <summary>
    /// Gets or sets the dead-letter queue that receives messages which exhausted <see cref="MaxRetries"/>.
    /// </summary>
    public string? DeadLetterQueue { get; set; }

    /// <summary>
    /// Initializes the attribute.
    /// </summary>
    /// <param name="topicOrExchange">Topic name for Kafka, exchange name for RabbitMQ.</param>
    public MessageAttribute(string topicOrExchange)
    {
        TopicOrExchange = topicOrExchange;
    }
}
