using Grpc.Core.Interceptors;

namespace LinKit.Grpc;

public interface IGrpcInterceptorProvider
{
    Interceptor[] GetInterceptorsFor<TClient>();
}
