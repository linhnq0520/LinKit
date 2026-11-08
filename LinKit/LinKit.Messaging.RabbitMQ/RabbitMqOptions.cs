namespace LinKit.Messaging.RabbitMQ;

/// <summary>
/// RabbitMQ connection, channel and delivery settings used by the LinKit.RabbitMQ adapters.
/// </summary>
/// <remarks>
/// Values are validated by <c>AddLinKitRabbitMQ</c> through <c>IValidateOptions&lt;RabbitMqOptions&gt;</c>, so a bad
/// configuration fails on first resolution instead of degrading silently.
/// </remarks>
public class RabbitMqOptions
{
    /// <summary>Broker host name or IP address.</summary>
    public string HostName { get; set; } = "localhost";

    /// <summary>User name used for the AMQP handshake.</summary>
    public string UserName { get; set; } = "guest";

    /// <summary>Password used for the AMQP handshake; supply it from a secret store in production.</summary>
    public string Password { get; set; } = "guest";

    /// <summary>AMQP port; 5672 for plain TCP and 5671 for TLS.</summary>
    public int Port { get; set; } = 5672;

    /// <summary>Virtual host to operate in; <c>/</c> is the default host.</summary>
    public string VirtualHost { get; set; } = "/";

    /// <summary>Number of unacknowledged deliveries allowed per consumer channel.</summary>
    public ushort PrefetchCount { get; set; } = 50;

    /// <summary>Size of the channel pool used by producers; larger values trade broker sockets for throughput.</summary>
    public int ChannelPoolSize { get; set; } = 64;

    /// <summary>
    /// When <see langword="true"/> a failed delivery is republished up to <see cref="MaxRetryAttempts"/> times before it
    /// is dead-lettered or dropped. When <see langword="false"/> the first failure goes straight to
    /// <see cref="DeadLetterExchange"/> (or is dropped) with no retry.
    /// </summary>
    /// <remarks>
    /// Retries are bounded, so the option can no longer create the poison-message loop that an unbounded requeue caused.
    /// </remarks>
    public bool RequeueOnFailure { get; set; } = true;

    /// <summary>Republish retries after a processing failure before the message is dead-lettered or dropped. 0 = no retries. Negative values are clamped to 0.</summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>Exchange that receives exhausted messages. Declared as durable/direct if absent; existing exchanges are used as-is. When unset, exhausted messages are nacked without requeue (queue-level DLX applies if configured).</summary>
    public string? DeadLetterExchange { get; set; }

    /// <summary>Exchange type declared when a message does not specify its own exchange.</summary>
    public string DefaultExchangeType { get; set; } = "topic";

    /// <summary>
    /// Waits for the broker acknowledgement after every publish. Disabling it raises throughput but allows silently
    /// dropped messages.
    /// </summary>
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
