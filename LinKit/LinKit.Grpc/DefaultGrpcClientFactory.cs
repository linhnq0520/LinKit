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

    private readonly ConcurrentDictionary<Type, Lazy<GrpcChannel>> _channels = new();
    private readonly ConcurrentDictionary<Type, CallInvoker> _invokers = new();
    private readonly ConcurrentDictionary<Type, ClientBase> _clients = new();

    public GrpcChannel GetChannelFor<TClient>()
        where TClient : ClientBase
    {
        // Lazy guarantees the provider factory runs once; GetOrAdd alone can invoke it
        // concurrently and leak the losing channels.
        return _channels
            .GetOrAdd(
                typeof(TClient),
                static (_, provider) =>
                    new Lazy<GrpcChannel>(() => provider.GetChannelFor<TClient>()),
                _channelProvider
            )
            .Value;
    }

    public Interceptor[] GetInterceptorsFor<TClient>() =>
        _interceptorProvider.GetInterceptorsFor<TClient>();

    public Metadata? GetMetadata() => _metadataProvider?.GetMetadata();

    public CallInvoker GetCallInvokerFor<TClient>()
        where TClient : ClientBase
    {
        return _invokers.GetOrAdd(
            typeof(TClient),
            _ =>
            {
                var channel = GetChannelFor<TClient>();
                var interceptors = GetInterceptorsFor<TClient>();
                var invoker = channel.CreateCallInvoker();
                if (interceptors != null && interceptors.Length > 0)
                {
                    invoker = invoker.Intercept(interceptors);
                }
                return invoker;
            }
        );
    }

    public TClient GetClient<TClient>()
        where TClient : ClientBase
    {
        return (TClient)
            _clients.GetOrAdd(
                typeof(TClient),
                _ =>
                {
                    // The source generator emits a module initializer that registers a reflection-free
                    // `new TClient(invoker)` delegate. Without it there is no trim/AOT-safe way to build the
                    // client, so fail loudly instead of silently falling back to Activator.CreateInstance.
                    var factory =
                        GrpcClientActivator<TClient>.Factory
                        ?? throw new InvalidOperationException(
                            $"No generated activation path is registered for gRPC client '{typeof(TClient).FullName}'. "
                                + "Reference the project that declares the [GrpcClient] request (or the assembly containing "
                                + $"LinKit.Grpc.Generator output) so the generated {nameof(GrpcClientActivator<TClient>)} registration "
                                + "is compiled in, or construct the client yourself with new TClient(factory.GetCallInvokerFor<TClient>())."
                        );

                    return factory(GetCallInvokerFor<TClient>());
                }
            );
    }

    public void Dispose()
    {
        // Providers are injected (typically owned by the DI container), so the factory must not dispose them.
        _clients.Clear();
        _invokers.Clear();
        _channels.Clear();
        GC.SuppressFinalize(this);
    }
}
