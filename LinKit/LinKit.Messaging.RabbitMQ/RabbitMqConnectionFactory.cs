using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace LinKit.Messaging.RabbitMQ;

public sealed class RabbitMqConnectionFactory : IDisposable
{
    private readonly ConnectionFactory _factory;
    private readonly ILogger<RabbitMqConnectionFactory>? _logger;
    private readonly object _syncRoot = new();
    private IConnection? _connection;
    private bool _disposed;

    public RabbitMqConnectionFactory(IServiceProvider serviceProvider, IOptions<RabbitMqOptions> options)
    {
        _logger = serviceProvider.GetService<ILogger<RabbitMqConnectionFactory>>();
        _factory = new ConnectionFactory
        {
            HostName = options.Value.HostName,
            UserName = options.Value.UserName,
            Password = options.Value.Password,
            Port = options.Value.Port,
            VirtualHost = string.IsNullOrEmpty(options.Value.VirtualHost) ? "/" : options.Value.VirtualHost,
            DispatchConsumersAsync = true,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(5)
        };
    }

    public IConnection GetConnection()
    {
        if (_connection is { IsOpen: true })
            return _connection;

        lock (_syncRoot)
        {
            if (_connection is { IsOpen: true })
                return _connection;

            try
            {
                _connection?.Dispose();
            }
            catch
            {
                // Ignore dispose errors on dead connection
            }

            _logger?.LogInformation("Connecting to RabbitMQ at {HostName}:{Port}...", _factory.HostName, _factory.Port);
            _connection = _factory.CreateConnection();
            return _connection;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _connection?.Dispose();
        }
        catch
        {
            // Ignore dispose errors
        }
    }
}
