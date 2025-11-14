using System.Collections.Concurrent;
using Grpc.Core;
using Grpc.Net.Client;

namespace LinKit.Grpc;

public class DefaultGrpcChannelProvider(string baseAddress) : IGrpcChannelProvider, IDisposable
{
    private readonly ConcurrentDictionary<Type, GrpcChannel> _channels = new();
    private readonly string _baseAddress = baseAddress;

    public GrpcChannel GetChannelFor<TClient>()
        where TClient : ClientBase
    {
        var clientType = typeof(TClient);

        return _channels.AddOrUpdate(
            clientType,
            (type) => GrpcChannel.ForAddress(_baseAddress),
            (type, existingChannel) =>
            {
                if (existingChannel.State == ConnectivityState.Shutdown)
                {
                    existingChannel.Dispose();
                    return GrpcChannel.ForAddress(_baseAddress);
                }

                return existingChannel;
            }
        );
    }

    public void Dispose()
    {
        foreach (var channel in _channels.Values)
        {
            channel?.Dispose();
        }
        _channels.Clear();
        GC.SuppressFinalize(this);
    }
}
