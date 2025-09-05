using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace LinKit.Messaging.RabbitMQ;

internal sealed class RabbitMqConnectionFactory : IDisposable
{
    private readonly IConnection _connection;
    public RabbitMqConnectionFactory(IServiceProvider serviceProvider, IOptions<RabbitMqOptions> options)
    {
        var logger = serviceProvider.GetService<ILogger<RabbitMqConnectionFactory>>();
        var factory = new ConnectionFactory
        {
            HostName = options.Value.HostName,
            UserName = options.Value.UserName,
            Password = options.Value.Password,
            Port = options.Value.Port,
            DispatchConsumersAsync = true
        };
        logger?.LogInformation("Connecting to RabbitMQ at {HostName}...", factory.HostName);
        _connection = factory.CreateConnection();
    }
    public IConnection GetConnection() => _connection;
    public void Dispose() => _connection?.Dispose();
}