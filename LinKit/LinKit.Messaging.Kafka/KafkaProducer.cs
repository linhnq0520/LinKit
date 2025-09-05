using Confluent.Kafka;
using LinKit.Core.Messaging;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace LinKit.Messaging.Kafka;

internal sealed class KafkaProducer : IBrokerProducer, IDisposable
{
    private readonly IProducer<string, byte[]> _producer;

    public KafkaProducer(IOptions<KafkaOptions> options)
    {
        var config = new ProducerConfig { BootstrapServers = options.Value.BootstrapServers };
        _producer = new ProducerBuilder<string, byte[]>(config).Build();
    }

    public async Task ProduceAsync(string topicOrExchange, string routingKey, object message, MessageHeaders? headers, CancellationToken ct)
    {
        var kafkaHeaders = new Headers();
        if (headers is not null)
        {
            foreach (var header in headers)
            {
                // Kafka headers are typically strings or byte arrays
                var valueBytes = header.Value switch
                {
                    string s => System.Text.Encoding.UTF8.GetBytes(s),
                    byte[] b => b,
                    _ => System.Text.Encoding.UTF8.GetBytes(header.Value.ToString() ?? string.Empty)
                };
                kafkaHeaders.Add(header.Key, valueBytes);
            }
        }

        var kafkaMessage = new Message<string, byte[]>
        {
            // Trong Kafka, routingKey thường được dùng làm Key của message
            Key = routingKey,
            Value = JsonSerializer.SerializeToUtf8Bytes(message),
            Headers = kafkaHeaders
        };

        // topicOrExchange tương ứng với Topic trong Kafka
        await _producer.ProduceAsync(topicOrExchange, kafkaMessage, ct);
    }

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(10));
        _producer.Dispose();
    }
}