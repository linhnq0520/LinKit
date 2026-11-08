namespace LinKit.Core.Messaging;

/// <summary>
/// Canonical names of the headers LinKit writes to and reads from broker messages. Every name is prefixed with
/// <c>linkit-</c> to avoid collisions with headers produced by other libraries.
/// </summary>
public static class MessageHeaderNames
{
    /// <summary>Assembly-qualified name of the message type, used to resolve the consumer.</summary>
    public const string MessageType = "linkit-message-type";

    /// <summary>Identifier shared by every message produced while handling one logical operation.</summary>
    public const string CorrelationId = "linkit-correlation-id";

    /// <summary>UTC creation time of the message.</summary>
    public const string Timestamp = "linkit-timestamp";

    /// <summary>Number of delivery attempts already made, used by retry and dead-letter policies.</summary>
    public const string RetryCount = "linkit-retry-count";

    /// <summary>Topic the message must be published to.</summary>
    public const string DestinationTopic = "linkit-destination-topic";

    /// <summary>Broker routing key, for example the RabbitMQ binding key.</summary>
    public const string RoutingKey = "linkit-routing-key";

    /// <summary>Partition key used by brokers that preserve ordering per key, for example Kafka.</summary>
    public const string PartitionKey = "linkit-partition-key";

    /// <summary>Exchange type requested when the destination exchange has to be declared.</summary>
    public const string ExchangeType = "linkit-exchange-type";

    /// <summary>Queue or subject a reply must be sent to, for request/response messaging.</summary>
    public const string ReplyTo = "linkit-reply-to";

    /// <summary>Unique identifier of the message, used for de-duplication and tracing.</summary>
    public const string MessageId = "linkit-message-id";

    /// <summary>
    /// The tenant that owns the message. Captured when an Outbox message is created and restored by the publisher
    /// before the broker call, so a tenant never leaks into another tenant's downstream work.
    /// </summary>
    public const string TenantId = "linkit-tenant-id";
}

/// <summary>
/// Case-insensitive header bag attached to a message. The strongly typed properties are shortcuts over the names in
/// <see cref="MessageHeaderNames"/>; assigning <see langword="null"/> leaves the bag unchanged, and raw keys remain
/// accessible through the dictionary interface.
/// </summary>
public class MessageHeaders : Dictionary<string, object>
{
    /// <summary>
    /// Initializes an empty header bag with an ordinal, case-insensitive key comparer.
    /// </summary>
    public MessageHeaders()
        : base(StringComparer.OrdinalIgnoreCase) { }

    /// <summary>
    /// Initializes a header bag from an existing set of headers, copying every entry.
    /// </summary>
    /// <param name="collection">Headers to copy; <see langword="null"/> produces an empty bag.</param>
    public MessageHeaders(IEnumerable<KeyValuePair<string, object>>? collection)
        : base(StringComparer.OrdinalIgnoreCase)
    {
        if (collection != null)
        {
            foreach (var kvp in collection)
            {
                this[kvp.Key] = kvp.Value;
            }
        }
    }

    /// <summary>
    /// Gets or sets the message type name stored in <see cref="MessageHeaderNames.MessageType"/>.
    /// </summary>
    public string? MessageType
    {
        get => TryGetValue(MessageHeaderNames.MessageType, out var val) ? val?.ToString() : null;
        set
        {
            if (value != null)
                this[MessageHeaderNames.MessageType] = value;
        }
    }

    /// <summary>
    /// Gets or sets the correlation identifier stored in <see cref="MessageHeaderNames.CorrelationId"/>.
    /// </summary>
    public string? CorrelationId
    {
        get => TryGetValue(MessageHeaderNames.CorrelationId, out var val) ? val?.ToString() : null;
        set
        {
            if (value != null)
                this[MessageHeaderNames.CorrelationId] = value;
        }
    }

    /// <summary>
    /// Gets or sets the destination topic stored in <see cref="MessageHeaderNames.DestinationTopic"/>.
    /// </summary>
    public string? DestinationTopic
    {
        get =>
            TryGetValue(MessageHeaderNames.DestinationTopic, out var val) ? val?.ToString() : null;
        set
        {
            if (value != null)
                this[MessageHeaderNames.DestinationTopic] = value;
        }
    }

    /// <summary>
    /// Gets or sets the routing key stored in <see cref="MessageHeaderNames.RoutingKey"/>.
    /// </summary>
    public string? RoutingKey
    {
        get => TryGetValue(MessageHeaderNames.RoutingKey, out var val) ? val?.ToString() : null;
        set
        {
            if (value != null)
                this[MessageHeaderNames.RoutingKey] = value;
        }
    }

    /// <summary>
    /// Gets or sets the tenant identifier stored in <see cref="MessageHeaderNames.TenantId"/>.
    /// </summary>
    public string? TenantId
    {
        get => TryGetValue(MessageHeaderNames.TenantId, out var val) ? val?.ToString() : null;
        set
        {
            if (value != null)
                this[MessageHeaderNames.TenantId] = value;
        }
    }
}

/// <summary>
/// Supplies extra headers for outgoing messages. Register an implementation to stamp tenant, trace or audit data on
/// every produced message without changing the call sites.
/// </summary>
public interface IMessageMetadataProvider
{
    /// <summary>
    /// Returns the headers to merge into the outgoing message.
    /// </summary>
    /// <typeparam name="TMessage">Message type being produced.</typeparam>
    /// <param name="message">The message instance being produced.</param>
    /// <returns>The headers to merge; return an empty bag when nothing should be added.</returns>
    MessageHeaders GetHeaders<TMessage>(TMessage message);
}

/// <summary>
/// Optional hook run by generated consumers around each message. Register implementations in DI
/// (for example to restore tenant context). The returned scope is disposed after the message is processed.
/// </summary>
public interface IMessageConsumeInterceptor
{
    /// <summary>
    /// Called before the handler runs and returns a scope that is disposed after it finishes.
    /// </summary>
    /// <param name="scopedServices">Scope that resolves the handler and the interceptor's own dependencies.</param>
    /// <param name="headers">Headers of the message being handled, for example to restore the tenant or trace context.</param>
    /// <returns>The scope to dispose afterwards, or <see langword="null"/> when there is nothing to clean up.</returns>
    IDisposable? Enter(
        IServiceProvider scopedServices,
        IReadOnlyDictionary<string, object> headers
    );
}

/// <summary>
/// Opens the scopes of every registered <see cref="IMessageConsumeInterceptor"/>, returning a single handle that
/// disposes them in reverse order.
/// </summary>
public static class MessageConsumeInterceptors
{
    /// <summary>
    /// Enters all interceptors registered in <paramref name="scopedServices"/>.
    /// </summary>
    /// <param name="scopedServices">Scope that resolves the message handler.</param>
    /// <param name="headers">Headers of the message being handled.</param>
    /// <returns>
    /// A handle that disposes every interceptor scope in reverse registration order. When an interceptor throws while
    /// entering, the scopes opened so far are disposed before the exception propagates.
    /// </returns>
    public static IDisposable Enter(
        IServiceProvider scopedServices,
        IReadOnlyDictionary<string, object> headers
    )
    {
        var entered = new List<IDisposable>();
        try
        {
            foreach (
                var interceptor in (IEnumerable<IMessageConsumeInterceptor>?)
                    scopedServices.GetService(typeof(IEnumerable<IMessageConsumeInterceptor>))
                    ?? Array.Empty<IMessageConsumeInterceptor>()
            )
            {
                var scope = interceptor.Enter(scopedServices, headers);
                if (scope is not null)
                    entered.Add(scope);
            }
        }
        catch
        {
            Dispose(entered);
            throw;
        }
        return new CompositeScope(entered);
    }

    private static void Dispose(List<IDisposable> scopes)
    {
        for (var i = scopes.Count - 1; i >= 0; i--)
            scopes[i].Dispose();
    }

    private sealed class CompositeScope(List<IDisposable> scopes) : IDisposable
    {
        public void Dispose() => MessageConsumeInterceptors.Dispose(scopes);
    }
}

/// <summary>
/// Publishes a message to a broker.
/// </summary>
public interface IBrokerProducer
{
    /// <summary>
    /// Publishes <paramref name="message"/> to the supplied destination.
    /// </summary>
    /// <param name="topicOrExchange">Topic name for Kafka, exchange name for RabbitMQ.</param>
    /// <param name="routingKey">Broker routing key; ignored by brokers without routing keys.</param>
    /// <param name="message">Message instance to serialize and publish.</param>
    /// <param name="headers">Optional headers to attach.</param>
    /// <param name="cancellationToken">Token that cancels the publish.</param>
    /// <returns>A task that completes when the broker acknowledged the message.</returns>
    Task ProduceAsync(
        string topicOrExchange,
        string routingKey,
        object message,
        MessageHeaders? headers,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Consumes messages from a broker. Implementations are registered as singletons because one connection serves every
/// subscription in the process.
/// </summary>
public interface IBrokerConnection
{
    /// <summary>
    /// Starts consuming from an existing queue and dispatches every delivery to
    /// <paramref name="onMessageReceived"/>.
    /// </summary>
    /// <param name="queueName">Queue to consume from.</param>
    /// <param name="onMessageReceived">
    /// Callback receiving the routing key, the raw body and the headers, and returning a task that represents the
    /// handling of one message.
    /// </param>
    /// <param name="stoppingToken">Token that stops consumption when the host shuts down.</param>
    /// <returns>A task that completes when consumption has started.</returns>
    Task StartConsumingAsync(
        string queueName,
        Func<
            string,
            ReadOnlyMemory<byte>,
            IReadOnlyDictionary<string, object>,
            Task
        > onMessageReceived,
        CancellationToken stoppingToken
    );

    /// <summary>
    /// Starts consuming after declaring the queue and, when <paramref name="exchangeName"/> is supplied, the exchange
    /// and its bindings.
    /// </summary>
    /// <param name="queueName">Queue to consume from; created when it does not exist.</param>
    /// <param name="exchangeName">Exchange to bind, or <see langword="null"/> to consume from a pre-provisioned queue.</param>
    /// <param name="exchangeType">Exchange type, for example <c>topic</c>, <c>direct</c> or <c>fanout</c>.</param>
    /// <param name="routingKeys">Routing keys to bind; <see langword="null"/> binds the queue name.</param>
    /// <param name="onMessageReceived">Callback that handles one delivery.</param>
    /// <param name="stoppingToken">Token that stops consumption when the host shuts down.</param>
    /// <param name="prefetchCount">Maximum number of unacknowledged deliveries per consumer.</param>
    /// <param name="durable">Whether the queue survives a broker restart.</param>
    /// <param name="exclusive">Whether the queue is limited to this connection.</param>
    /// <param name="autoDelete">Whether the queue is removed when the last consumer disconnects.</param>
    /// <param name="queueArguments">Broker specific queue arguments, for example a dead-letter exchange.</param>
    /// <returns>A task that completes when consumption has started.</returns>
    Task StartConsumingAsync(
        string queueName,
        string? exchangeName,
        string exchangeType,
        IEnumerable<string>? routingKeys,
        Func<
            string,
            ReadOnlyMemory<byte>,
            IReadOnlyDictionary<string, object>,
            Task
        > onMessageReceived,
        CancellationToken stoppingToken,
        ushort prefetchCount = 10,
        bool durable = true,
        bool exclusive = false,
        bool autoDelete = false,
        IDictionary<string, object?>? queueArguments = null
    ) => StartConsumingAsync(queueName, onMessageReceived, stoppingToken);
}
