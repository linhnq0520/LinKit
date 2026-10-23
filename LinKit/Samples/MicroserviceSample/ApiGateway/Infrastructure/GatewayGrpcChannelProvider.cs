using System.Collections.Concurrent;
using Grpc.Core;
using Grpc.Net.Client;
using LinKit.Grpc;

namespace MicroserviceSample.ApiGateway.Infrastructure;

public class GatewayGrpcChannelProvider(IConfiguration configuration) : IGrpcChannelProvider, IDisposable
{
    private readonly ConcurrentDictionary<Type, GrpcChannel> _channels = new();

    public GrpcChannel GetChannelFor<TClient>() where TClient : ClientBase
    {
        return _channels.GetOrAdd(typeof(TClient), type =>
        {
            var address = configuration[$"GrpcClients:{type.Name}"] ?? "http://localhost:5005";
            return GrpcChannel.ForAddress(address);
        });
    }

    public void Dispose()
    {
        foreach (var ch in _channels.Values)
        {
            ch.Dispose();
        }
        _channels.Clear();
    }
}
