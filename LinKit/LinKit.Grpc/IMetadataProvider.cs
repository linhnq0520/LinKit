using Grpc.Core;

namespace LinKit.Grpc;

/// <summary>
/// Provides dynamic metadata (headers) for outgoing gRPC calls.
/// </summary>
public interface IMetadataProvider
{
    /// <summary>
    /// Gets the metadata to be attached to the gRPC call.
    /// This is called for every request.
    /// </summary>
    Metadata GetMetadata();
}
