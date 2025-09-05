using Grpc.Core;

namespace LinKit.Grpc;

public static class GrpcContextAccessor
{
    private static readonly AsyncLocal<ServerCallContext?> _current = new();

    public static ServerCallContext? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}
