namespace LinKit.Messaging.RabbitMQ;

public class RabbitMqOptions
{
    public string HostName { get; set; } = "localhost";
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public int Port { get; set; } = 5672;
    public string VirtualHost { get; set; } = "/";
    public ushort PrefetchCount { get; set; } = 50;
    public int ChannelPoolSize { get; set; } = 64;
    public bool RequeueOnFailure { get; set; } = true;
}