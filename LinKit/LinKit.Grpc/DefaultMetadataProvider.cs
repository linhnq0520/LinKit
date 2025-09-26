using Grpc.Core;

namespace LinKit.Grpc;

public class DefaultMetadataProvider : IMetadataProvider
{
    public Metadata? GetMetadata() => null;
}
