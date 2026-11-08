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

    /// <summary>
    /// Collects configuration problems; the list is empty when the options are usable. The registered
    /// <c>IValidateOptions&lt;RabbitMqOptions&gt;</c> runs this on first resolve so a bad configuration fails
    /// visibly instead of degrading silently.
    /// </summary>
    internal IReadOnlyList<string> Validate()
    {
        List<string> errors = [];

        if (string.IsNullOrWhiteSpace(HostName))
            errors.Add("HostName must not be empty.");
        if (Port is < 1 or > 65535)
            errors.Add("Port must be between 1 and 65535.");
        if (VirtualHost is null)
            errors.Add("VirtualHost must not be null; use \"/\" for the default virtual host.");
        if (string.IsNullOrWhiteSpace(UserName))
            errors.Add("UserName must not be empty.");
        if (ChannelPoolSize <= 0)
            errors.Add("ChannelPoolSize must be greater than zero.");
        if (!IsKnownExchangeType(DefaultExchangeType))
            errors.Add("DefaultExchangeType must be one of direct, topic, fanout or headers.");

        return errors;
    }

    private static bool IsKnownExchangeType(string value) =>
        value.Equals("direct", StringComparison.OrdinalIgnoreCase)
        || value.Equals("topic", StringComparison.OrdinalIgnoreCase)
        || value.Equals("fanout", StringComparison.OrdinalIgnoreCase)
        || value.Equals("headers", StringComparison.OrdinalIgnoreCase);
}
