using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace LinKit.Messaging.RabbitMQ;

/// <summary>
/// Owns the single long-lived AMQP connection used by producers and consumers.
/// </summary>
/// <remarks>
/// The connection is created lazily on first use and recreated when it is no longer open, so a broker restart does not
/// require an application restart. Automatic and topology recovery are enabled.
/// </remarks>
public sealed class RabbitMqConnectionFactory : IDisposable
{
    private readonly ConnectionFactory _factory;
    private readonly ILogger<RabbitMqConnectionFactory>? _logger;
    private readonly object _syncRoot = new();
    private IConnection? _connection;
    private bool _disposed;

    /// <summary>Creates the factory from validated options.</summary>
    /// <param name="serviceProvider">Provider used to resolve the optional logger.</param>
    /// <param name="options">Broker settings; an empty virtual host falls back to <c>/</c>.</param>
    public RabbitMqConnectionFactory(
        IServiceProvider serviceProvider,
        IOptions<RabbitMqOptions> options
    )
    {
        _logger = serviceProvider.GetService<ILogger<RabbitMqConnectionFactory>>();
        _factory = new ConnectionFactory
        {
            HostName = options.Value.HostName,
            UserName = options.Value.UserName,
            Password = options.Value.Password,
            Port = options.Value.Port,
            VirtualHost = string.IsNullOrEmpty(options.Value.VirtualHost)
                ? "/"
                : options.Value.VirtualHost,
            DispatchConsumersAsync = true,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
        };
    }

    /// <summary>Returns the open connection, connecting (or reconnecting) when necessary.</summary>
    /// <returns>A connection whose <c>IsOpen</c> is <see langword="true"/>.</returns>
    /// <exception cref="global::RabbitMQ.Client.Exceptions.BrokerUnreachableException">The broker cannot be reached.</exception>
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

            _logger?.LogInformation(
                "Connecting to RabbitMQ at {HostName}:{Port}...",
                _factory.HostName,
                _factory.Port
            );
            _connection = _factory.CreateConnection();
            return _connection;
        }
    }

    /// <summary>Disposes the underlying connection. Dispose errors are ignored because the socket may already be gone.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
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
