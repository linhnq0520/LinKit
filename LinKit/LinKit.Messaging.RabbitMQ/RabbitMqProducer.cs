using System.Collections.Concurrent;
using LinKit.Core.Messaging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace LinKit.Messaging.RabbitMQ;

public sealed class RabbitMqProducer : IBrokerProducer, IDisposable
{
    private readonly RabbitMqConnectionFactory _connectionFactory;
    private readonly ConcurrentBag<IModel> _channelPool = new();
    private readonly ConcurrentDictionary<string, string> _declaredExchanges = new(StringComparer.Ordinal);
    private readonly int _maxPoolSize;
    private readonly string _defaultExchangeType;
    private readonly bool _publisherConfirms;

    public RabbitMqProducer(RabbitMqConnectionFactory connectionFactory, IOptions<RabbitMqOptions>? options = null)
    {
        _connectionFactory = connectionFactory;
        _maxPoolSize = options?.Value.ChannelPoolSize > 0 ? options.Value.ChannelPoolSize : 64;
        _defaultExchangeType = !string.IsNullOrWhiteSpace(options?.Value.DefaultExchangeType)
            ? options.Value.DefaultExchangeType
            : ExchangeType.Topic;
        _publisherConfirms = options?.Value.PublisherConfirms ?? true;
    }

    public Task ProduceAsync(
        string topicOrExchange,
        string routingKey,
        object message,
        MessageHeaders? headers,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(message);

        var channel = RentChannel();
        try
        {
            string exchangeType = _defaultExchangeType;
            if (headers is not null && headers.TryGetValue(MessageHeaderNames.ExchangeType, out var extObj) && extObj is string extStr && !string.IsNullOrWhiteSpace(extStr))
            {
                exchangeType = extStr;
            }

            // Declare exchange only once across application lifetime
            if (!string.IsNullOrEmpty(topicOrExchange))
            {
                _declaredExchanges.GetOrAdd(topicOrExchange, exchangeName =>
                {
                    channel.ExchangeDeclare(exchange: exchangeName, type: exchangeType, durable: true);
                    return exchangeType;
                });
            }

            var body = LinKitMessageJson.SerializeToUtf8Bytes(message);

            var properties = channel.CreateBasicProperties();
            properties.Persistent = true;

            var messageType = message.GetType().Name;
            var headerDict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                [MessageHeaderNames.MessageType] = messageType,
                [MessageHeaderNames.Timestamp] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            if (headers is not null && headers.Count > 0)
            {
                foreach (var header in headers)
                {
                    if (string.Equals(header.Key, MessageHeaderNames.CorrelationId, StringComparison.OrdinalIgnoreCase) && header.Value != null)
                        properties.CorrelationId = header.Value.ToString();
                    else if (string.Equals(header.Key, MessageHeaderNames.ReplyTo, StringComparison.OrdinalIgnoreCase) && header.Value != null)
                        properties.ReplyTo = header.Value.ToString();
                    else if (string.Equals(header.Key, MessageHeaderNames.MessageId, StringComparison.OrdinalIgnoreCase) && header.Value != null)
                        properties.MessageId = header.Value.ToString();

                    if (header.Value is not null)
                    {
                        headerDict[header.Key] = header.Value;
                    }
                }
            }

            properties.Headers = headerDict;

            channel.BasicPublish(
                exchange: topicOrExchange ?? string.Empty,
                routingKey: routingKey ?? string.Empty,
                basicProperties: properties,
                body: body
            );

            if (_publisherConfirms)
            {
                // Publisher confirm: wait for broker ACK to ensure message is persisted
                channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(5));
            }

            return Task.CompletedTask;
        }
        finally
        {
            ReturnChannel(channel);
        }
    }

    public IModel RentChannel()
    {
        while (_channelPool.TryTake(out var channel))
        {
            if (channel.IsOpen)
            {
                return channel;
            }
            channel.Dispose();
        }

        var newChannel = _connectionFactory.GetConnection().CreateModel();
        if (_publisherConfirms)
        {
            newChannel.ConfirmSelect();
        }
        return newChannel;
    }

    public void ReturnChannel(IModel channel)
    {
        if (channel.IsOpen && _channelPool.Count < _maxPoolSize)
        {
            _channelPool.Add(channel);
        }
        else
        {
            try
            {
                channel.Dispose();
            }
            catch
            {
                // Ignore disposal errors on closed channels
            }
        }
    }

    public void Dispose()
    {
        while (_channelPool.TryTake(out var channel))
        {
            try
            {
                channel.Dispose();
            }
            catch
            {
                // Ignore disposal errors
            }
        }
    }
}
