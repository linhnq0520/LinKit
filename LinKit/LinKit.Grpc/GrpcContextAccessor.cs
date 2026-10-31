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
        var scope = new Scope(_current.Value);
        _current.Value = context;
        return scope;
    }
    public static void Reset() => _current.Value = null;

    public sealed class Scope : IDisposable
    {
        private readonly ServerCallContext? _previous;
        private int _disposed;

        internal Scope(ServerCallContext? previous) => _previous = previous;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _current.Value = _previous;
            }
        }
    }
}