using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;

namespace LinKit.Grpc;

public interface IGrpcClientFactory
{
    GrpcChannel GetChannelFor<TClient>()
        where TClient : ClientBase;
    Interceptor[] GetInterceptorsFor<TClient>();
    Metadata? GetMetadata();
}
