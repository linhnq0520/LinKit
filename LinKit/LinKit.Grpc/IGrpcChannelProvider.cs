using Grpc.Core;
using Grpc.Net.Client;

namespace LinKit.Grpc;

/// <summary>
/// Provides a mechanism to get the appropriate GrpcChannel for a given gRPC client type.
/// </summary>
public interface IGrpcChannelProvider
{
    /// <summary>
    /// Gets the GrpcChannel for the specified gRPC client.
    /// </summary>
    /// <typeparam name="TClient">The type of the gRPC client (e.g., UserService.UserClient).</typeparam>
    /// <returns>The configured GrpcChannel.</returns>
    GrpcChannel GetChannelFor<TClient>()
        where TClient : ClientBase;
}
