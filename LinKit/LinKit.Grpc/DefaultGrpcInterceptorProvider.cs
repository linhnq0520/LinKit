using Grpc.Core.Interceptors;

namespace LinKit.Grpc;

public class DefaultGrpcInterceptorProvider : IGrpcInterceptorProvider
{
    public Interceptor[] GetInterceptorsFor<TClient>() => [];
}
