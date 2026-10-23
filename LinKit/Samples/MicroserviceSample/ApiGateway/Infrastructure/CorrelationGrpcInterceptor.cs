using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Logging;

namespace MicroserviceSample.ApiGateway.Infrastructure;

public class CorrelationGrpcInterceptor(ILogger<CorrelationGrpcInterceptor> logger) : Interceptor
{
    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        logger.LogInformation("[Gateway Interceptor] Gắn CorrelationId '{CorrelationId}' vào Header gRPC call tới {Method}", correlationId, context.Method.Name);

        var headers = context.Options.Headers ?? new Metadata();
        headers.Add("x-correlation-id", correlationId);
        headers.Add("x-client-source", "ApiGateway");

        var newOptions = context.Options.WithHeaders(headers);
        var newContext = new ClientInterceptorContext<TRequest, TResponse>(context.Method, context.Host, newOptions);

        return continuation(request, newContext);
    }
}
