using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;

namespace LinKit.Grpc;

/// <summary>
/// Creates fully configured gRPC clients for a generated client type: channel, interceptors and metadata are resolved
/// from the registered providers, so callers never assemble a <see cref="CallInvoker"/> by hand.
/// </summary>
public interface IGrpcClientFactory
{
    /// <summary>Returns the shared channel used for <typeparamref name="TClient"/>.</summary>
    /// <typeparam name="TClient">Generated gRPC client type.</typeparam>
    GrpcChannel GetChannelFor<TClient>()
        where TClient : ClientBase;

    /// <summary>Returns the interceptors applied to every call made by <typeparamref name="TClient"/>.</summary>
    /// <typeparam name="TClient">Generated gRPC client type.</typeparam>
    Interceptor[] GetInterceptorsFor<TClient>();

    /// <summary>Returns the headers attached to outgoing calls, or <see langword="null"/> when none are configured.</summary>
    Metadata? GetMetadata();

    /// <summary>Builds the invoker that combines the channel, interceptors and headers for <typeparamref name="TClient"/>.</summary>
    /// <typeparam name="TClient">Generated gRPC client type.</typeparam>
    CallInvoker GetCallInvokerFor<TClient>()
        where TClient : ClientBase;

    /// <summary>Creates or returns the registered instance of <typeparamref name="TClient"/>.</summary>
    /// <typeparam name="TClient">Generated gRPC client type.</typeparam>
    TClient GetClient<TClient>()
        where TClient : ClientBase;
}
