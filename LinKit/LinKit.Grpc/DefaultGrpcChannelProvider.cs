using System.Collections.Concurrent;
using Grpc.Core;
using Grpc.Net.Client;

namespace LinKit.Grpc;

public class DefaultGrpcChannelProvider(string baseAddress) : IGrpcChannelProvider, IDisposable
{
    private readonly ConcurrentDictionary<Type, GrpcChannel> _channels = new();
    private readonly string _baseAddress = baseAddress;

    public GrpcChannel GetChannelFor<TClient>()
        where TClient : ClientBase =>
        _channels.GetOrAdd(typeof(TClient), _ => GrpcChannel.ForAddress(_baseAddress));

    public void Dispose()
    {
        foreach (var channel in _channels.Values)
        {
            channel.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
