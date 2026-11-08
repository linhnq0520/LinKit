using LinKit.Core.Cqrs;

namespace LinKit.Grpc;

/// <summary>
/// Transport-neutral entry point generated gRPC endpoints use to dispatch CQRS requests into the in-process mediator.
/// </summary>
public interface IGrpcMediator
{
    /// <summary>Sends a command that produces no result.</summary>
    /// <param name="command">Command to dispatch.</param>
    /// <param name="cancellationToken">Token forwarded to the handler.</param>
    Task SendAsync(ICommand command, CancellationToken cancellationToken = default);

    /// <summary>Sends a command and returns its response.</summary>
    /// <typeparam name="TResponse">Response type produced by the handler.</typeparam>
    /// <param name="command">Command to dispatch.</param>
    /// <param name="cancellationToken">Token forwarded to the handler.</param>
    Task<TResponse> SendAsync<TResponse>(
        ICommand<TResponse> command,
        CancellationToken cancellationToken = default
    );

    /// <summary>Executes a query and returns its response.</summary>
    /// <typeparam name="TResponse">Result type produced by the handler.</typeparam>
    /// <param name="query">Query to execute.</param>
    /// <param name="cancellationToken">Token forwarded to the handler.</param>
    Task<TResponse> QueryAsync<TResponse>(
        IQuery<TResponse> query,
        CancellationToken cancellationToken = default
    );
}
