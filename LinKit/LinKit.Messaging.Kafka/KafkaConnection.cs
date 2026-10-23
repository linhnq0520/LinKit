using System.Text;
using Confluent.Kafka;
using LinKit.Core.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LinKit.Messaging.Kafka;

internal sealed class KafkaConnection : IBrokerConnection, IDisposable
{
    public void Dispose()
    {
        _dlqProducer?.Flush(TimeSpan.FromSeconds(5));
        _dlqProducer?.Dispose();
    }

    private readonly ILogger<KafkaConnection>? _logger;
    private readonly ConsumerConfig _consumerConfig;
    private static readonly TimeSpan ProcessingRetryDelay = TimeSpan.FromSeconds(1);
    private readonly int _maxAttempts;
    private readonly string _dlqSuffix;
    private readonly ProducerConfig _dlqProducerConfig;
    private IProducer<string, byte[]>? _dlqProducer;
    private readonly object _dlqLock = new();

    public KafkaConnection(IOptions<KafkaOptions> options, ILogger<KafkaConnection>? logger = null)
    {
        _logger = logger;
        var opt = options.Value;
        _maxAttempts = opt.MaxProcessingAttempts;
        _dlqSuffix = opt.DeadLetterTopicSuffix;
        _dlqProducerConfig = new ProducerConfig
        {
            BootstrapServers = opt.BootstrapServers,
            Acks = Acks.All,
            EnableIdempotence = true
        };
        _consumerConfig = new ConsumerConfig
        {
            BootstrapServers = opt.BootstrapServers,
            GroupId = opt.GroupId,
            AutoOffsetReset = opt.AutoOffsetReset,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false
        };

        if (opt.EnableAutoCommit)
        {
            _logger?.LogWarning(
                "Kafka auto-commit is disabled by LinKit so offsets are committed only after successful message processing.");
        }
    }

    private async Task PublishToDeadLetterAsync(ConsumeResult<string, byte[]> record, Exception error, CancellationToken ct)
    {
        IProducer<string, byte[]> producer;
        lock (_dlqLock)
        {
            producer = _dlqProducer ??= new ProducerBuilder<string, byte[]>(_dlqProducerConfig).Build();
        }

        var headers = new Headers();
        if (record.Message.Headers != null)
        {
            foreach (var h in record.Message.Headers)
                headers.Add(h.Key, h.GetValueBytes());
        }
        headers.Add("x-original-topic", Encoding.UTF8.GetBytes(record.Topic));
        headers.Add("x-original-partition", Encoding.UTF8.GetBytes(record.Partition.Value.ToString()));
        headers.Add("x-original-offset", Encoding.UTF8.GetBytes(record.Offset.Value.ToString()));
        headers.Add("x-error", Encoding.UTF8.GetBytes(error.GetType().Name + ": " + error.Message));

        await producer.ProduceAsync(
            record.Topic + _dlqSuffix,
            new Message<string, byte[]> { Key = record.Message.Key, Value = record.Message.Value, Headers = headers },
            ct);
    }

    public Task StartConsumingAsync(
        string queueName,
        Func<string, ReadOnlyMemory<byte>, IReadOnlyDictionary<string, object>, Task> onMessageReceived,
        CancellationToken stoppingToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        ArgumentNullException.ThrowIfNull(onMessageReceived);

        return Task.Run(async () =>
        {
            using var consumer = new ConsumerBuilder<string, byte[]>(_consumerConfig).Build();
            consumer.Subscribe(queueName);
            _logger?.LogInformation("Kafka consumer started. Subscribed to topic: {Topic}", queueName);

            try
            {
                TopicPartitionOffset? lastRecord = null;
                int attempt = 0;
                while (!stoppingToken.IsCancellationRequested)
                {
                    ConsumeResult<string, byte[]>? consumeResult = null;
                    try
                    {
                        consumeResult = consumer.Consume(stoppingToken);
                    }
                    catch (ConsumeException ex)
                    {
                        _logger?.LogError(ex, "Kafka consume error: {Reason}", ex.Error.Reason);
                        await Task.Delay(ProcessingRetryDelay, stoppingToken);
                        continue;
                    }

                    if (consumeResult?.Message is null)
                    {
                        continue;
                    }

                    var headers = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    if (consumeResult.Message.Headers != null)
                    {
                        foreach (var header in consumeResult.Message.Headers)
                        {
                            var bytes = header.GetValueBytes();
                            headers[header.Key] = bytes != null ? Encoding.UTF8.GetString(bytes) : string.Empty;
                        }
                    }

                    // Pass value directly as ReadOnlyMemory<byte>
                    ReadOnlyMemory<byte> bodyMemory = consumeResult.Message.Value;

                    var tpo = consumeResult.TopicPartitionOffset;
                    attempt = lastRecord != null && lastRecord.Equals(tpo) ? attempt + 1 : 1;
                    lastRecord = tpo;
                    var record = consumeResult;

                    await KafkaRecordProcessor.ProcessAsync(
                        () => onMessageReceived(consumeResult.Message.Key ?? string.Empty, bodyMemory, headers),
                        () => consumer.Commit(consumeResult),
                        () => consumer.Seek(consumeResult.TopicPartitionOffset),
                        consumeResult.Topic,
                        consumeResult.Message.Key,
                        ProcessingRetryDelay,
                        stoppingToken,
                        _logger,
                        attempt,
                        _maxAttempts,
                        ex => PublishToDeadLetterAsync(record, ex, stoppingToken));
                }
            }
            catch (OperationCanceledException)
            {
                _logger?.LogInformation("Kafka consumer stopping.");
            }
            finally
            {
                try
                {
                    consumer.Close();
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Error while closing Kafka consumer.");
                }
            }
        }, CancellationToken.None);
    }
}