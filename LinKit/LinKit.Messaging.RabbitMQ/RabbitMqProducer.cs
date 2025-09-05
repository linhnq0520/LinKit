using LinKit.Core.Messaging;
using RabbitMQ.Client;
using System.Text.Json;

namespace LinKit.Messaging.RabbitMQ;

internal sealed class RabbitMqProducer : IBrokerProducer
{
    private readonly RabbitMqConnectionFactory _connectionFactory;
    public RabbitMqProducer(RabbitMqConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public Task ProduceAsync(string topicOrExchange, string routingKey, object message, MessageHeaders? headers, CancellationToken ct)
    {
        using var connection = _connectionFactory.GetConnection();
        using var channel = connection.CreateModel();

        channel.ExchangeDeclare(exchange: topicOrExchange, type: ExchangeType.Topic, durable: true);
        var body = JsonSerializer.SerializeToUtf8Bytes(message);

        var properties = channel.CreateBasicProperties();
        properties.Persistent = true;

        if (headers is not null && headers.Count > 0)
        {
            properties.Headers = new Dictionary<string, object>();
            foreach (var header in headers)
            {
                properties.Headers[header.Key] = header.Value;
            }
        }

        channel.BasicPublish(exchange: topicOrExchange, routingKey: routingKey, basicProperties: properties, body: body);
        return Task.CompletedTask;
    }
}