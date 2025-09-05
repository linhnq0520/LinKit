namespace LinKit.Messaging.Kafka;

public class KafkaOptions
{
    public string BootstrapServers { get; set; } = "localhost:9092";
    public string GroupId { get; set; } = "linkit-consumer-group";
}