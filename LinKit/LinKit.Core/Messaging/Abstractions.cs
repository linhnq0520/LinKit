namespace LinKit.Core.Messaging;

public class MessageHeaders : Dictionary<string, object>
{
    public MessageHeaders() : base(StringComparer.OrdinalIgnoreCase) { }
}

public interface IMessageMetadataProvider
{
    MessageHeaders GetHeaders<TMessage>(TMessage message);
}

public interface IBrokerProducer
{
    Task ProduceAsync(
        string topicOrExchange,
        string routingKey,
        object message,
        MessageHeaders? headers,
        CancellationToken ct);
}

public interface IBrokerConnection
{
    Task StartConsumingAsync(
        string queueName,
        Func<string, byte[], IReadOnlyDictionary<string, object>, Task> onMessageReceived,
        CancellationToken stoppingToken);
}