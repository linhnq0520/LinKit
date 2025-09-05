using Confluent.Kafka;
using LinKit.Core.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LinKit.Messaging.Kafka;

internal sealed class KafkaConnection : IBrokerConnection
{
    private readonly ILogger<KafkaConnection>? _logger;
    private readonly ConsumerConfig _consumerConfig;
    private readonly List<string> _topicsToSubscribe = new();

    public KafkaConnection(IOptions<KafkaOptions> options, ILogger<KafkaConnection>? logger = null)
    {
        _logger = logger;
        _consumerConfig = new ConsumerConfig
        {
            BootstrapServers = options.Value.BootstrapServers,
            GroupId = options.Value.GroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false // Để kiểm soát commit thủ công
        };
    }

    public Task StartConsumingAsync(
        string queueName, // Trong Kafka, queueName tương ứng với Topic
        Func<string, byte[], IReadOnlyDictionary<string, object>, Task> onMessageReceived,
        CancellationToken stoppingToken)
    {
        // Kafka consumer có thể subscribe nhiều topic, nên ta chỉ cần 1 consumer
        // Chúng ta sẽ khởi động consumer trong một Task chạy nền riêng.
        // Tuy nhiên, để đơn giản, chúng ta sẽ gộp logic vào đây.
        // Một thiết kế tốt hơn sẽ có một IHostedService riêng để quản lý consumer.

        _topicsToSubscribe.Add(queueName); // `queueName` là topic

        // Chạy consumer trong một Task mới để không block ExecuteAsync
        _ = Task.Run(async () =>
        {
            using var consumer = new ConsumerBuilder<string, byte[]>(_consumerConfig).Build();
            // Subscribe vào tất cả các topic đã được yêu cầu
            consumer.Subscribe(_topicsToSubscribe.Distinct());
            _logger?.LogInformation("Kafka consumer started. Subscribed to topics: {Topics}", string.Join(", ", _topicsToSubscribe.Distinct()));

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    var consumeResult = consumer.Consume(stoppingToken);

                    try
                    {
                        var headers = consumeResult.Message.Headers
                            .ToDictionary(h => h.Key, h => (object)h.GetValueBytes());

                        // routingKey của message chính là Key trong Kafka
                        await onMessageReceived(consumeResult.Message.Key, consumeResult.Message.Value, headers);

                        // Commit offset sau khi xử lý thành công
                        consumer.Commit(consumeResult);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Error processing Kafka message from topic {Topic}.", consumeResult.Topic);
                        // Không commit, message sẽ được xử lý lại sau một khoảng thời gian
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger?.LogInformation("Kafka consumer stopping.");
            }
            finally
            {
                consumer.Close();
            }
        }, stoppingToken);

        return Task.CompletedTask;
    }
}