namespace LinKit.Messaging.RabbitMQ;

public class RabbitMqOptions
{
    public string HostName { get; set; } = "localhost";
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public int Port { get; set; } = 5672;
    public string VirtualHost { get; set; } = "/";
    public ushort PrefetchCount { get; set; } = 50;
    public int ChannelPoolSize { get; set; } = 64;
    public bool RequeueOnFailure { get; set; } = true;

    /// <summary>Republish retries after a processing failure before the message is dead-lettered or dropped. 0 = no retries. Negative values are clamped to 0.</summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>Exchange that receives exhausted messages. Declared as durable/direct if absent; existing exchanges are used as-is. When unset, exhausted messages are nacked without requeue (queue-level DLX applies if configured).</summary>
    public string? DeadLetterExchange { get; set; }

    public string DefaultExchangeType { get; set; } = "topic";
    public bool PublisherConfirms { get; set; } = true;
}
