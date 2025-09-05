using LinKit.Core.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace LinKit.Messaging.RabbitMQ;

internal sealed class RabbitMqConnection : IBrokerConnection
{
    private readonly IConnection _connection;
    private readonly ILogger<RabbitMqConnection>? _logger;

    public RabbitMqConnection(RabbitMqConnectionFactory connectionFactory, IServiceProvider serviceProvider)
    {
        _connection = connectionFactory.GetConnection();
        _logger = serviceProvider.GetService<ILogger<RabbitMqConnection>>();
    }

    public Task StartConsumingAsync(
        string queueName,
        Func<string, byte[], IReadOnlyDictionary<string, object>, Task> onMessageReceived,
        CancellationToken stoppingToken)
    {
        _logger?.LogInformation("Starting RabbitMQ consumer for queue '{QueueName}'...", queueName);
        var channel = _connection.CreateModel();
        channel.BasicQos(0, 1, false);
        var consumer = new AsyncEventingBasicConsumer(channel);

        consumer.Received += async (model, ea) =>
        {
            var headers = ea.BasicProperties.Headers ?? new Dictionary<string, object>();
            try
            {
                await onMessageReceived(ea.RoutingKey, ea.Body.ToArray(), (IReadOnlyDictionary<string, object>)headers);
                channel.BasicAck(ea.DeliveryTag, false);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error processing message from queue '{QueueName}'.", queueName);
                channel.BasicNack(ea.DeliveryTag, false, false);
            }
        };
        channel.BasicConsume(queue: queueName, autoAck: false, consumer: consumer);
        return Task.CompletedTask;
    }
}