using System.Collections.Concurrent;
using System.Text.Json;
using LinKit.Core.Messaging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace LinKit.Messaging.RabbitMQ;

internal sealed class RabbitMqProducer : IBrokerProducer, IDisposable
{
    private readonly RabbitMqConnectionFactory _connectionFactory;
    private readonly ConcurrentBag<IModel> _channelPool = new();
    private readonly ConcurrentDictionary<string, bool> _declaredExchanges = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maxPoolSize;

    public RabbitMqProducer(RabbitMqConnectionFactory connectionFactory, IOptions<RabbitMqOptions>? options = null)
    {
        _connectionFactory = connectionFactory;
        _maxPoolSize = options?.Value.ChannelPoolSize > 0 ? options.Value.ChannelPoolSize : 64;
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
            // Declare exchange only once across application lifetime
            if (!string.IsNullOrEmpty(topicOrExchange))
            {
                _declaredExchanges.GetOrAdd(topicOrExchange, exchangeName =>
                {
                    channel.ExchangeDeclare(exchange: exchangeName, type: ExchangeType.Topic, durable: true);
                    return true;
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
                    headerDict[header.Key] = header.Value;
                }
            }

            properties.Headers = headerDict;

            channel.BasicPublish(
                exchange: topicOrExchange ?? string.Empty,
                routingKey: routingKey ?? string.Empty,
                basicProperties: properties,
                body: body
            );

            // Publisher confirm: wait for broker ACK to ensure message is persisted
            channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(5));

            return Task.CompletedTask;
        }
        finally
        {
            ReturnChannel(channel);
        }
    }

    private IModel RentChannel()
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
        newChannel.ConfirmSelect();
        return newChannel;
    }

    private void ReturnChannel(IModel channel)
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
