using Grpc.Core.Interceptors;

namespace LinKit.Grpc;

/// <summary>
/// Interceptor provider that adds nothing, so clients call the server without client-side interceptors.
/// </summary>
public class DefaultGrpcInterceptorProvider : IGrpcInterceptorProvider
{
    /// <returns>An empty array, meaning no interception.</returns>
    public Interceptor[] GetInterceptorsFor<TClient>() => [];
}
