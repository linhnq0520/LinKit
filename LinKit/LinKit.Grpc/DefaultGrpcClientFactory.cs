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

    private readonly ConcurrentDictionary<Type, GrpcChannel> _channels = new();
    private readonly ConcurrentDictionary<Type, CallInvoker> _invokers = new();
    private readonly ConcurrentDictionary<Type, ClientBase> _clients = new();

    public GrpcChannel GetChannelFor<TClient>()
        where TClient : ClientBase
    {
        return _channels.GetOrAdd(typeof(TClient), _ => _channelProvider.GetChannelFor<TClient>());
    }

    public Interceptor[] GetInterceptorsFor<TClient>() =>
        _interceptorProvider.GetInterceptorsFor<TClient>();

    public Metadata? GetMetadata() => _metadataProvider?.GetMetadata();

    public CallInvoker GetCallInvokerFor<TClient>()
        where TClient : ClientBase
    {
        return _invokers.GetOrAdd(typeof(TClient), _ =>
        {
            var channel = GetChannelFor<TClient>();
            var interceptors = GetInterceptorsFor<TClient>();
            var invoker = channel.CreateCallInvoker();
            if (interceptors != null && interceptors.Length > 0)
            {
                invoker = invoker.Intercept(interceptors);
            }
            return invoker;
        });
    }

    public TClient GetClient<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors)] TClient>()
        where TClient : ClientBase
    {
        return (TClient)_clients.GetOrAdd(typeof(TClient), _ =>
        {
            var invoker = GetCallInvokerFor<TClient>();

            // Preferred: generated, reflection-free factory.
            var factory = GrpcClientActivator<TClient>.Factory;
            if (factory != null)
            {
                return factory(invoker);
            }

            // Fallback for clients not seen by the generator (not Native AOT safe; trimmer keeps public constructors via annotation).
            try
            {
                var instance = Activator.CreateInstance(typeof(TClient), invoker);
                if (instance is TClient client)
                {
                    return client;
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to create gRPC client instance of type {typeof(TClient).FullName}. Ensure it has a constructor accepting CallInvoker.",
                    ex
                );
            }

            throw new InvalidOperationException($"Type {typeof(TClient).FullName} could not be activated as {nameof(ClientBase)}.");
        });
    }

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
