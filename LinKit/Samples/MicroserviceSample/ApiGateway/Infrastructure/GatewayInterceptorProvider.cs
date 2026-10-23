using Grpc.Core;
using Grpc.Core.Interceptors;
using LinKit.Grpc;

namespace MicroserviceSample.ApiGateway.Infrastructure;

public class GatewayInterceptorProvider(CorrelationGrpcInterceptor correlationInterceptor) : IGrpcInterceptorProvider
{
    public Interceptor[] GetInterceptorsFor<TClient>()
    {
        return [correlationInterceptor];
    }
}
