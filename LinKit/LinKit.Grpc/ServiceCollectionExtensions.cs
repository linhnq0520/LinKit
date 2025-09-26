using Microsoft.Extensions.DependencyInjection;

namespace LinKit.Grpc;

public static class ServiceCollectionExtensions
{
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

    public static IServiceCollection AddGrpcMetadataProvider<T>(this IServiceCollection services)
        where T : class, IMetadataProvider
    {
        return services.AddSingleton<IMetadataProvider, T>();
    }

    public static IServiceCollection AddGrpcInterceptorProvider<T>(this IServiceCollection services)
        where T : class, IGrpcInterceptorProvider
    {
        return services.AddSingleton<IGrpcInterceptorProvider, T>();
    }

    public static IServiceCollection AddGrpcChannelProvider<T>(this IServiceCollection services)
        where T : class, IGrpcChannelProvider
    {
        return services.AddSingleton<IGrpcChannelProvider, T>();
    }
}
