using System.Text;
using LinKit.Core.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace LinKit.Messaging.RabbitMQ;

internal sealed class RabbitMqConnection : IBrokerConnection
{
    private readonly IConnection _connection;
    private readonly ILogger<RabbitMqConnection>? _logger;
    private readonly ushort _prefetchCount;
    private readonly bool _requeueOnFailure;

    public RabbitMqConnection(
        RabbitMqConnectionFactory connectionFactory,
        IServiceProvider serviceProvider,
        IOptions<RabbitMqOptions>? options = null
    )
    {
        _connection = connectionFactory.GetConnection();
        _logger = serviceProvider.GetService<ILogger<RabbitMqConnection>>();
        _prefetchCount = options?.Value.PrefetchCount > 0 ? options.Value.PrefetchCount : (ushort)50;
        _requeueOnFailure = options?.Value.RequeueOnFailure ?? true;
    }

    public async Task StartConsumingAsync(
        string queueName,
        Func<string, ReadOnlyMemory<byte>, IReadOnlyDictionary<string, object>, Task> onMessageReceived,
        CancellationToken stoppingToken
    )
    {
        _logger?.LogInformation("Starting RabbitMQ consumer for queue '{QueueName}' with PrefetchCount={PrefetchCount}...", queueName, _prefetchCount);

        var channel = _connection.CreateModel();
        channel.BasicQos(0, _prefetchCount, false);

        channel.QueueDeclare(
            queue: queueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null
        );

        var consumer = new AsyncEventingBasicConsumer(channel);

        consumer.Received += async (model, ea) =>
        {
            var rawHeaders = ea.BasicProperties.Headers;
            var headers = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            if (rawHeaders != null)
            {
                foreach (var kvp in rawHeaders)
                {
                    // RabbitMQ stores string headers as UTF8 byte arrays
                    if (kvp.Value is byte[] byteArr)
                    {
                        headers[kvp.Key] = Encoding.UTF8.GetString(byteArr);
                    }
                    else
                    {
                        headers[kvp.Key] = kvp.Value;
                    }
                }
            }

            try
            {
                // Pass ea.Body directly as ReadOnlyMemory<byte> (zero allocation, no ToArray())
                await onMessageReceived(
                    ea.RoutingKey,
                    ea.Body,
                    headers
                );

                channel.BasicAck(ea.DeliveryTag, false);
            }
            catch (Exception ex)
            {
                _logger?.LogError(
                    ex,
                    "Error processing message from queue '{QueueName}' (RoutingKey: '{RoutingKey}').",
                    queueName,
                    ea.RoutingKey
                );

                // Nack without requeue if poison message or reject
                channel.BasicNack(ea.DeliveryTag, false, _requeueOnFailure);
            }
        };

        var consumerTag = channel.BasicConsume(queue: queueName, autoAck: false, consumer: consumer);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            try
            {
                _logger?.LogInformation("Stopping RabbitMQ consumer for queue '{QueueName}'...", queueName);
                if (channel.IsOpen)
                {
                    channel.BasicCancel(consumerTag);
                    channel.Close();
                }
                channel.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error while closing consumer channel for queue '{QueueName}'.", queueName);
            }
        }

    }
}
