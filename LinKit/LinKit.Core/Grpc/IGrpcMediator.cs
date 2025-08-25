using LinKit.Core.Cqrs;

namespace LinKit.Core.Grpc;

public interface IGrpcMediator
{
    Task SendAsync<TCommand>(TCommand command, CancellationToken ct = default)
        where TCommand : ICommand;
    Task<TResult> SendAsync<TCommand, TResult>(TCommand command, CancellationToken ct = default)
        where TCommand : ICommand<TResult>;
    Task<TResult> QueryAsync<TQuery, TResult>(TQuery query, CancellationToken ct = default)
        where TQuery : IQuery<TResult>;
}
