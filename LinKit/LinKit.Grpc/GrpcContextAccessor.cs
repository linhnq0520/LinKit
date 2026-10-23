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

    public static Scope BeginScope(ServerCallContext? context)
    {
        _current.Value = context;
        return new Scope();
    }

    public static void Reset() => _current.Value = null;

    public readonly struct Scope : IDisposable
    {
        public void Dispose()
        {
            _current.Value = null;
        }
    }
}
