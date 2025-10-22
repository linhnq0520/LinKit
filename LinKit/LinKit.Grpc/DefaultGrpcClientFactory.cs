using System.Collections.Concurrent;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;

namespace LinKit.Grpc;

public class DefaultGrpcClientFactory(
    IGrpcChannelProvider channelProvider,
    IGrpcInterceptorProvider interceptorProvider,
    IMetadataProvider? metadataProvider = null
) : IGrpcClientFactory, IDisposable
{
    private readonly IGrpcChannelProvider _channelProvider = channelProvider;
    private readonly IGrpcInterceptorProvider _interceptorProvider = interceptorProvider;
    private readonly IMetadataProvider? _metadataProvider = metadataProvider;

    private ConcurrentDictionary<Type, GrpcChannel> _channels = new();

    public GrpcChannel GetChannelFor<TClient>()
        where TClient : ClientBase
    {
        return _channels.GetOrAdd(typeof(TClient), _ => _channelProvider.GetChannelFor<TClient>());
    }

    public Interceptor[] GetInterceptorsFor<TClient>() =>
        _interceptorProvider.GetInterceptorsFor<TClient>();

    public Metadata? GetMetadata() => _metadataProvider?.GetMetadata();

    public void Dispose()
    {
        if (_channelProvider is IDisposable d1)
        {
            d1.Dispose();
        }

        if (_interceptorProvider is IDisposable d2)
        {
            d2.Dispose();
        }

        if (_metadataProvider is IDisposable d3)
        {
            d3.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
