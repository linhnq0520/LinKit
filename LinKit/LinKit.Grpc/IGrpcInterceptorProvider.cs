using Grpc.Core.Interceptors;

namespace LinKit.Grpc;

/// <summary>
/// Supplies the interceptors applied to outgoing gRPC calls. Suppliers can vary them per client type, for example to
/// add tracing only where it is needed.
/// </summary>
public interface IGrpcInterceptorProvider
{
    /// <summary>Returns the interceptors to install for <typeparamref name="TClient"/>.</summary>
    /// <typeparam name="TClient">Generated gRPC client type.</typeparam>
    /// <returns>The interceptors in invocation order; an empty array means no interception.</returns>
    Interceptor[] GetInterceptorsFor<TClient>();
}
