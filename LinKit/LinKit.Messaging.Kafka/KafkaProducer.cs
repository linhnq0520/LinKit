using System.Globalization;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using LinKit.Core.Messaging;
using Microsoft.Extensions.Options;

namespace LinKit.Messaging.Kafka;

internal sealed class KafkaProducer : IBrokerProducer, IDisposable
{
    private readonly IProducer<string, byte[]> _producer;

    public KafkaProducer(IOptions<KafkaOptions> options)
    {
        var opt = options.Value;
        var config = new ProducerConfig
        {
            BootstrapServers = opt.BootstrapServers,
            LingerMs = opt.LingerMs,
            BatchSize = opt.BatchSize,
            CompressionType = opt.CompressionType,
            Acks = Acks.All,
            EnableIdempotence = true
        };
        _producer = new ProducerBuilder<string, byte[]>(config).Build();
    }

    public async Task ProduceAsync(
        string topicOrExchange,
        string routingKey,
        object message,
        MessageHeaders? headers,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(message);

        var kafkaHeaders = new Headers();
        var messageType = headers?.MessageType ?? message.GetType().Name;

        // Ensure linkit standard headers
        kafkaHeaders.Add(MessageHeaderNames.MessageType, Encoding.UTF8.GetBytes(messageType));
        byte[] timestampBytes;
        if (headers != null && headers.TryGetValue(MessageHeaderNames.Timestamp, out var timestampValue))
        {
            timestampBytes = timestampValue switch
            {
                byte[] bytes => bytes,
                IFormattable formattable => Encoding.UTF8.GetBytes(
                    formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty),
                _ => Encoding.UTF8.GetBytes(timestampValue?.ToString() ?? string.Empty)
            };
        }
        else
        {
            timestampBytes = Encoding.UTF8.GetBytes(
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        }
        kafkaHeaders.Add(MessageHeaderNames.Timestamp, timestampBytes);

        if (headers is not null)
        {
            foreach (var header in headers)
            {
                if (string.Equals(header.Key, MessageHeaderNames.MessageType, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var valueBytes = header.Value switch
                {
                    byte[] b => b,
                    string s => Encoding.UTF8.GetBytes(s),
                    _ => Encoding.UTF8.GetBytes(header.Value?.ToString() ?? string.Empty)
                };
                if (string.Equals(header.Key, MessageHeaderNames.Timestamp, StringComparison.OrdinalIgnoreCase))
                    continue;
                kafkaHeaders.Add(header.Key, valueBytes);
            }
        }

        var messageKey = headers is not null &&
                         headers.TryGetValue(MessageHeaderNames.PartitionKey, out var partitionKey)
            ? partitionKey?.ToString() ?? string.Empty
            : routingKey ?? string.Empty;

        var kafkaMessage = new Message<string, byte[]>
        {
            Key = messageKey,
            Value = LinKitMessageJson.SerializeToUtf8Bytes(message),
            Headers = kafkaHeaders,
            Timestamp = new Timestamp(DateTimeOffset.UtcNow)
        };

        await _producer.ProduceAsync(topicOrExchange, kafkaMessage, cancellationToken);
    }

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
    }
}
