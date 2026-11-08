using Grpc.Core;

namespace LinKit.Grpc;

/// <summary>
/// Metadata provider that contributes no headers; replace it to forward tokens or correlation ids.
/// </summary>
public class DefaultMetadataProvider : IMetadataProvider
{
    /// <returns>Always <see langword="null"/>, meaning no extra headers are attached.</returns>
    public Metadata? GetMetadata() => null;
}
