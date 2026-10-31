using System.Text;
using LinKit.Core.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace LinKit.Messaging.RabbitMQ;

public sealed class RabbitMqConnection : IBrokerConnection
{
    private readonly RabbitMqConnectionFactory _connectionFactory;
    private readonly ILogger<RabbitMqConnection>? _logger;
    private readonly ushort _prefetchCount;
    private readonly bool _requeueOnFailure;

    public RabbitMqConnection(
        RabbitMqConnectionFactory connectionFactory,
        IServiceProvider serviceProvider,
        IOptions<RabbitMqOptions>? options = null
    )
    {
        _connectionFactory = connectionFactory;
        _logger = serviceProvider.GetService<ILogger<RabbitMqConnection>>();
        _prefetchCount = options?.Value.PrefetchCount > 0 ? options.Value.PrefetchCount : (ushort)50;
        _requeueOnFailure = options?.Value.RequeueOnFailure ?? true;
    }

    private static bool QueueExists(IConnection connection, string queueName)
    {
        try
        {
            using var probe = connection.CreateModel();
            probe.QueueDeclarePassive(queueName);
            return true;
        }
        catch (global::RabbitMQ.Client.Exceptions.OperationInterruptedException)
        {
            return false;
        }
    }

    public Task StartConsumingAsync(
        string queueName,
        Func<string, ReadOnlyMemory<byte>, IReadOnlyDictionary<string, object>, Task> onMessageReceived,
        CancellationToken stoppingToken
    ) => StartConsumingAsync(
        queueName: queueName,
        exchangeName: null,
        exchangeType: ExchangeType.Topic,
        routingKeys: null,
        onMessageReceived: onMessageReceived,
        stoppingToken: stoppingToken,
        prefetchCount: _prefetchCount,
        durable: true,
        exclusive: false,
        autoDelete: false,
        queueArguments: null
    );

    public async Task StartConsumingAsync(
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
    )
    {
        ushort effectivePrefetch = prefetchCount > 0 ? prefetchCount : _prefetchCount;
        _logger?.LogInformation(
            "Starting RabbitMQ consumer for queue '{QueueName}' (Exchange='{Exchange}', PrefetchCount={PrefetchCount})...",
            queueName,
            exchangeName ?? "(default)",
            effectivePrefetch
        );

        var connection = _connectionFactory.GetConnection();
        var channel = connection.CreateModel();
        channel.BasicQos(0, effectivePrefetch, false);

        if (!string.IsNullOrWhiteSpace(exchangeName))
        {
            channel.ExchangeDeclare(
                exchange: exchangeName,
                type: string.IsNullOrWhiteSpace(exchangeType) ? ExchangeType.Direct : exchangeType,
                durable: true,
                autoDelete: false
            );
        }

        IDictionary<string, object>? args = null;
        if (queueArguments != null)
        {
            args = new Dictionary<string, object>();
            foreach (var kv in queueArguments)
            {
                if (kv.Value != null)
                    args[kv.Key] = kv.Value;
            }
        }

        string actualQueueName;
        if (!string.IsNullOrEmpty(queueName) && QueueExists(connection, queueName))
        {
            // Queue already exists (possibly created by another component with different
            // arguments, e.g. auto_delete). Attach to it as-is to avoid PRECONDITION_FAILED.
            actualQueueName = queueName;
        }
        else
        {
            var declareOk = channel.QueueDeclare(
                queue: queueName ?? string.Empty,
                durable: durable,
                exclusive: exclusive,
                autoDelete: autoDelete,
                arguments: args
            );
            actualQueueName = declareOk.QueueName;
        }

        if (!string.IsNullOrWhiteSpace(exchangeName))
        {
            bool boundAny = false;
            if (routingKeys != null)
            {
                foreach (var rk in routingKeys)
                {
                    if (!string.IsNullOrWhiteSpace(rk))
                    {
                        channel.QueueBind(queue: actualQueueName, exchange: exchangeName, routingKey: rk);
                        boundAny = true;
                    }
                }
            }

            if (!boundAny && !string.IsNullOrWhiteSpace(actualQueueName))
            {
                channel.QueueBind(queue: actualQueueName, exchange: exchangeName, routingKey: actualQueueName);
            }
        }

        var consumer = new AsyncEventingBasicConsumer(channel);

        consumer.Received += async (_, ea) =>
        {
            var rawHeaders = ea.BasicProperties?.Headers;
            var headers = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            if (rawHeaders != null)
            {
                foreach (var kvp in rawHeaders)
                {
                    if (kvp.Value is byte[] byteArr)
                    {
                        headers[kvp.Key] = Encoding.UTF8.GetString(byteArr);
                    }
                    else if (kvp.Value != null)
                    {
                        headers[kvp.Key] = kvp.Value;
                    }
                }
            }

            if (!string.IsNullOrEmpty(ea.BasicProperties?.CorrelationId))
                headers[MessageHeaderNames.CorrelationId] = ea.BasicProperties.CorrelationId;
            if (!string.IsNullOrEmpty(ea.BasicProperties?.ReplyTo))
                headers[MessageHeaderNames.ReplyTo] = ea.BasicProperties.ReplyTo;
            if (!string.IsNullOrEmpty(ea.BasicProperties?.MessageId))
                headers[MessageHeaderNames.MessageId] = ea.BasicProperties.MessageId;

            try
            {
                await onMessageReceived(
                    ea.RoutingKey,
                    ea.Body,
                    headers
                );

                if (channel.IsOpen)
                {
                    channel.BasicAck(ea.DeliveryTag, false);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(
                    ex,
                    "Error processing message from queue '{QueueName}' (RoutingKey: '{RoutingKey}').",
                    actualQueueName,
                    ea.RoutingKey
                );

                if (channel.IsOpen)
                {
                    channel.BasicNack(ea.DeliveryTag, false, _requeueOnFailure);
                }
            }
        };

        var consumerTag = channel.BasicConsume(queue: actualQueueName, autoAck: false, consumer: consumer);

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
                _logger?.LogInformation("Stopping RabbitMQ consumer for queue '{QueueName}'...", actualQueueName);
                if (channel.IsOpen)
                {
                    channel.BasicCancel(consumerTag);
                    channel.Close();
                }
                channel.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error while closing consumer channel for queue '{QueueName}'.", actualQueueName);
            }
        }
    }
}
