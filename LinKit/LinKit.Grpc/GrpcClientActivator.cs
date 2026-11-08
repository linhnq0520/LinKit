using Grpc.Core;

namespace LinKit.Grpc;

/// <summary>
/// AOT-safe client construction. The source generator registers a <c>new TClient(invoker)</c> delegate per client type
/// from a module initializer, so no reflection (Activator) is required.
/// </summary>
public static class GrpcClientActivator<TClient>
    where TClient : ClientBase
{
    private static Func<CallInvoker, TClient>? _factory;

    /// <summary>
    /// Registers the generated construction delegate. Called from the generated module initializer, so application code
    /// normally never calls this directly.
    /// </summary>
    /// <param name="factory">Delegate that creates the client from a <see cref="CallInvoker"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    public static void Register(Func<CallInvoker, TClient> factory) =>
        Volatile.Write(ref _factory, factory ?? throw new ArgumentNullException(nameof(factory)));

    internal static Func<CallInvoker, TClient>? Factory => Volatile.Read(ref _factory);
}
