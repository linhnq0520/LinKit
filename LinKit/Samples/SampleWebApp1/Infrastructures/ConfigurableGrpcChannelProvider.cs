using System.Collections.Concurrent;
using Grpc.Core;
using Grpc.Net.Client;
using LinKit.Core.Grpc;

namespace SampleWebApp1.Infrastructures;

public class ConfigurableGrpcChannelProvider : IGrpcChannelProvider, IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly ConcurrentDictionary<Type, GrpcChannel> _channels = new();

    public ConfigurableGrpcChannelProvider(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public GrpcChannel GetChannelFor<TClient>()
        where TClient : ClientBase
    {
        return _channels.GetOrAdd(
            typeof(TClient),
            type =>
            {
                var address =
                    _configuration[$"GrpcClients:{type.Name}"]
                    ?? throw new InvalidOperationException(
                        $"Address for gRPC client {type.Name} not configured."
                    );

                var options = new GrpcChannelOptions
                {
                    HttpHandler = new SocketsHttpHandler
                    {
                        SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                        {
                            RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
                            {
                                return true;
                            },
                        },
                    },
                };

                return GrpcChannel.ForAddress(address, options);
            }
        );
    }

    public void Dispose()
    {
        foreach (var channel in _channels.Values)
        {
            channel.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
