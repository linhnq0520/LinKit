using Grpc.Core;

namespace LinKit.Grpc;

/// <summary>
/// Ambient access to the current server <see cref="ServerCallContext"/>, so handlers can read incoming headers without
/// threading the context through every signature.
/// </summary>
/// <remarks>
/// The value is stored in an <see cref="AsyncLocal{T}"/>, so it flows with the asynchronous call and never leaks across
/// concurrent requests. Dispose the scope returned by <see cref="BeginScope"/> to restore the previous value.
/// </remarks>
public static class GrpcContextAccessor
{
    private static readonly AsyncLocal<ServerCallContext?> _current = new();

    /// <summary>The server call context of the current request, or <see langword="null"/> outside a gRPC call.</summary>
    public static ServerCallContext? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }

    /// <summary>Makes <paramref name="context"/> ambient until the returned scope is disposed.</summary>
    /// <param name="context">Context to expose, or <see langword="null"/> to clear it for the scope.</param>
    /// <returns>A scope that restores the previous ambient context when disposed.</returns>
    public static Scope BeginScope(ServerCallContext? context)
    {
        var scope = new Scope(_current.Value);
        _current.Value = context;
        return scope;
    }

    /// <summary>Clears the ambient context without restoring a previous value.</summary>
    public static void Reset() => _current.Value = null;

    /// <summary>
    /// Restores the ambient context captured by <see cref="BeginScope"/>. Disposing twice is harmless.
    /// </summary>
    public sealed class Scope : IDisposable
    {
        private readonly ServerCallContext? _previous;
        private int _disposed;

        internal Scope(ServerCallContext? previous) => _previous = previous;

        /// <summary>Restores the context that was ambient before the scope started.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _current.Value = _previous;
            }
        }
    }
}