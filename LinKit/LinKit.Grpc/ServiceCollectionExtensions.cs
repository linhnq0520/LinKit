using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LinKit.Grpc;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the whole default gRPC client graph: a channel provider that reuses one channel per client type,
    /// plus default interceptor provider, metadata provider and client factory. Register your own
    /// <see cref="IGrpcChannelProvider"/> or <see cref="IGrpcInterceptorProvider"/> afterwards to override a default.
    /// </summary>
    public static IServiceCollection AddLinKitGrpc(
        this IServiceCollection services,
        string baseAddress
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseAddress);

        services.TryAddSingleton<IGrpcInterceptorProvider, DefaultGrpcInterceptorProvider>();
        services.TryAddSingleton<IMetadataProvider, DefaultMetadataProvider>();
        services.TryAddSingleton<IGrpcClientFactory, DefaultGrpcClientFactory>();
        services.AddSingleton<IGrpcChannelProvider>(new DefaultGrpcChannelProvider(baseAddress));

        return services;
    }

    /// <summary>
    /// Registers only the channel provider. Prefer <see cref="AddLinKitGrpc"/>, which also supplies the interceptor
    /// provider, metadata provider and client factory defaults.
    /// </summary>
    public static IServiceCollection AddGrpcChannel(
        this IServiceCollection services,
        string baseAddress
    )
    {
        services.AddSingleton<IGrpcChannelProvider>(sp => new DefaultGrpcChannelProvider(
            baseAddress
        ));

        return services;
    }

    public static IServiceCollection AddGrpcMetadataProvider<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T
    >(this IServiceCollection services)
        where T : class, IMetadataProvider
    {
        return services.AddSingleton<IMetadataProvider, T>();
    }

    public static IServiceCollection AddGrpcInterceptorProvider<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T
    >(this IServiceCollection services)
        where T : class, IGrpcInterceptorProvider
    {
        return services.AddSingleton<IGrpcInterceptorProvider, T>();
    }

    public static IServiceCollection AddGrpcChannelProvider<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T
    >(this IServiceCollection services)
        where T : class, IGrpcChannelProvider
    {
        return services.AddSingleton<IGrpcChannelProvider, T>();
    }
}
