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
    private readonly int _maxRetryAttempts;
    private readonly string? _deadLetterExchange;

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
        _maxRetryAttempts = Math.Max(0, options?.Value.MaxRetryAttempts ?? 3);
        _deadLetterExchange = string.IsNullOrWhiteSpace(options?.Value.DeadLetterExchange) ? null : options!.Value.DeadLetterExchange;
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

    private static bool ExchangeExists(IConnection connection, string exchangeName)
    {
        try
        {
            using var probe = connection.CreateModel();
            probe.ExchangeDeclarePassive(exchangeName);
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
        // IModel is not thread-safe; concurrent Received handlers must serialize every channel operation.
        using var channelLock = new SemaphoreSlim(1, 1);
        string actualQueueName = queueName ?? string.Empty;
        try
        {
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

            if (_deadLetterExchange is not null && !ExchangeExists(connection, _deadLetterExchange))
            {
                channel.ExchangeDeclare(exchange: _deadLetterExchange, type: ExchangeType.Direct, durable: true, autoDelete: false);
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

                var effectiveRoutingKey =
                    headers.TryGetValue("x-linkit-original-routing-key", out var origRkObj) && origRkObj is string origRk && !string.IsNullOrEmpty(origRk)
                        ? origRk
                        : ea.RoutingKey;

                try
                {
                    await onMessageReceived(
                        effectiveRoutingKey,
                        ea.Body,
                        headers
                    );

                    await channelLock.WaitAsync();
                    try
                    {
                        if (channel.IsOpen)
                        {
                            channel.BasicAck(ea.DeliveryTag, false);
                        }
                    }
                    finally
                    {
                        channelLock.Release();
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(
                        ex,
                        "Error processing message from queue '{QueueName}' (RoutingKey: '{RoutingKey}').",
                        actualQueueName,
                        effectiveRoutingKey
                    );

                    await HandleFailureAsync(ea, headers, effectiveRoutingKey);
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
                    await channelLock.WaitAsync();
                    try
                    {
                        if (channel.IsOpen)
                        {
                            channel.BasicCancel(consumerTag);
                            channel.Close();
                        }
                        channel.Dispose();
                    }
                    finally
                    {
                        channelLock.Release();
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Error while closing consumer channel for queue '{QueueName}'.", actualQueueName);
                }
            }
        }
        catch
        {
            try
            {
                channel.Dispose();
            }
            catch
            {
                // Ignore disposal errors on a channel that failed during setup
            }
            throw;
        }

        async Task HandleFailureAsync(BasicDeliverEventArgs ea, IReadOnlyDictionary<string, object> headers, string originalRoutingKey)
        {
            var attemptCount = RabbitMqRetryPolicy.GetAttemptCount(headers);
            var action = RabbitMqRetryPolicy.DecideFailureAction(
                attemptCount,
                _maxRetryAttempts,
                _requeueOnFailure,
                _deadLetterExchange is not null
            );

            await channelLock.WaitAsync();
            try
            {
                if (!channel.IsOpen)
                    return;

                if (action == RabbitMqFailureAction.Drop)
                {
                    _logger?.LogWarning(
                        "Message from queue '{QueueName}' (RoutingKey: '{RoutingKey}') failed after {Attempts} attempt(s) and will not be requeued; it is dead-lettered by the broker if the queue has a DLX, otherwise dropped.",
                        actualQueueName,
                        originalRoutingKey,
                        attemptCount + 1
                    );
                    channel.BasicNack(ea.DeliveryTag, false, false);
                    return;
                }

                // When retrying, publish directly to the failing consumer queue via the default exchange ("")
                // so retries do not broadcast duplicate messages to other queues bound to the same exchange.
                var useDirectQueueRetry = action == RabbitMqFailureAction.Retry && !string.IsNullOrEmpty(actualQueueName);
                var targetExchange = action == RabbitMqFailureAction.DeadLetter
                    ? _deadLetterExchange!
                    : (useDirectQueueRetry ? string.Empty : ea.Exchange);
                var targetRoutingKey = useDirectQueueRetry ? actualQueueName! : originalRoutingKey;

                var properties = channel.CreateBasicProperties();
                if (ea.BasicProperties is not null)
                {
                    properties.Persistent = ea.BasicProperties.Persistent;
                    properties.DeliveryMode = ea.BasicProperties.DeliveryMode;
                    properties.CorrelationId = ea.BasicProperties.CorrelationId;
                    properties.ReplyTo = ea.BasicProperties.ReplyTo;
                    properties.MessageId = ea.BasicProperties.MessageId;
                    properties.ContentType = ea.BasicProperties.ContentType;
                    properties.ContentEncoding = ea.BasicProperties.ContentEncoding;
                    properties.Type = ea.BasicProperties.Type;
                }

                var retryHeaders = ea.BasicProperties?.Headers is null
                    ? new Dictionary<string, object>()
                    : new Dictionary<string, object>(ea.BasicProperties.Headers);
                retryHeaders[RabbitMqRetryPolicy.AttemptHeader] = attemptCount + 1;
                if (useDirectQueueRetry && !string.IsNullOrEmpty(originalRoutingKey))
                {
                    retryHeaders["x-linkit-original-routing-key"] = originalRoutingKey;
                }
                properties.Headers = retryHeaders;

                try
                {
                    channel.BasicPublish(
                        exchange: targetExchange,
                        routingKey: targetRoutingKey,
                        basicProperties: properties,
                        body: ea.Body
                    );
                    channel.BasicAck(ea.DeliveryTag, false);

                    if (action == RabbitMqFailureAction.DeadLetter)
                    {
                        _logger?.LogWarning(
                            "Message from queue '{QueueName}' (RoutingKey: '{RoutingKey}') exhausted {MaxAttempts} retry attempt(s); dead-lettered to exchange '{DeadLetterExchange}'.",
                            actualQueueName,
                            originalRoutingKey,
                            _maxRetryAttempts,
                            _deadLetterExchange
                        );
                    }
                }
                catch (Exception publishEx)
                {
                    // Keep at-least-once: if the retry/DLX publish fails, requeue the original instead of losing it.
                    _logger?.LogError(
                        publishEx,
                        "Failed to {Action} message from queue '{QueueName}' (RoutingKey: '{RoutingKey}'); requeueing the original.",
                        action == RabbitMqFailureAction.DeadLetter ? "dead-letter" : "retry",
                        actualQueueName,
                        originalRoutingKey
                    );
                    if (channel.IsOpen)
                    {
                        channel.BasicNack(ea.DeliveryTag, false, true);
                    }
                }
            }
            finally
            {
                channelLock.Release();
            }
        }
    }
}
