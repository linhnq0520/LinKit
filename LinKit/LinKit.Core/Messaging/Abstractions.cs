namespace LinKit.Core.Messaging;

public static class MessageHeaderNames
{
    public const string MessageType = "linkit-message-type";
    public const string CorrelationId = "linkit-correlation-id";
    public const string Timestamp = "linkit-timestamp";
    public const string RetryCount = "linkit-retry-count";
    public const string DestinationTopic = "linkit-destination-topic";
    public const string PartitionKey = "linkit-partition-key";
    public const string ExchangeType = "linkit-exchange-type";
    public const string ReplyTo = "linkit-reply-to";
    public const string MessageId = "linkit-message-id";
}

public class MessageHeaders : Dictionary<string, object>
{
    public MessageHeaders()
        : base(StringComparer.OrdinalIgnoreCase) { }

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

    public string? MessageType
    {
        get => TryGetValue(MessageHeaderNames.MessageType, out var val) ? val?.ToString() : null;
        set { if (value != null) this[MessageHeaderNames.MessageType] = value; }
    }

    public string? CorrelationId
    {
        get => TryGetValue(MessageHeaderNames.CorrelationId, out var val) ? val?.ToString() : null;
        set { if (value != null) this[MessageHeaderNames.CorrelationId] = value; }
    }

    public string? DestinationTopic
    {
        get => TryGetValue(MessageHeaderNames.DestinationTopic, out var val) ? val?.ToString() : null;
        set { if (value != null) this[MessageHeaderNames.DestinationTopic] = value; }
    }
}

public interface IMessageMetadataProvider
{
    MessageHeaders GetHeaders<TMessage>(TMessage message);
}

/// <summary>
/// Optional hook run by generated consumers around each message. Register implementations in DI
/// (for example to restore tenant context). The returned scope is disposed after the message is processed.
/// </summary>
public interface IMessageConsumeInterceptor
{
    IDisposable? Enter(IServiceProvider scopedServices, IReadOnlyDictionary<string, object> headers);
}

public static class MessageConsumeInterceptors
{
    public static IDisposable Enter(IServiceProvider scopedServices, IReadOnlyDictionary<string, object> headers)
    {
        var entered = new List<IDisposable>();
        try
        {
            foreach (var interceptor in (IEnumerable<IMessageConsumeInterceptor>?)
                         scopedServices.GetService(typeof(IEnumerable<IMessageConsumeInterceptor>))
                     ?? Array.Empty<IMessageConsumeInterceptor>())
            {
                var scope = interceptor.Enter(scopedServices, headers);
                if (scope is not null) entered.Add(scope);
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
        for (var i = scopes.Count - 1; i >= 0; i--) scopes[i].Dispose();
    }

    private sealed class CompositeScope(List<IDisposable> scopes) : IDisposable
    {
        public void Dispose() => MessageConsumeInterceptors.Dispose(scopes);
    }
}

public interface IBrokerProducer
{
    Task ProduceAsync(
        string topicOrExchange,
        string routingKey,
        object message,
        MessageHeaders? headers,
        CancellationToken cancellationToken
    );
}

public interface IBrokerConnection
{
    Task StartConsumingAsync(
        string queueName,
        Func<string, ReadOnlyMemory<byte>, IReadOnlyDictionary<string, object>, Task> onMessageReceived,
        CancellationToken stoppingToken
    );

    Task StartConsumingAsync(
        string queueName,
        string? exchangeName,
        string exchangeType,
        IEnumerable<string>? routingKeys,
        Func<string, ReadOnlyMemory<byte>, IReadOnlyDictionary<string, object>, Task> onMessageReceived,
        CancellationToken stoppingToken,
        ushort prefetchCount = 10,
        bool durable = true,
        bool exclusive = false,
        bool autoDelete = false,
        IDictionary<string, object?>? queueArguments = null
    ) => StartConsumingAsync(queueName, onMessageReceived, stoppingToken);
}
