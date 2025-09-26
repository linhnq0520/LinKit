namespace LinKit.Core.Cqrs;

public interface IMediator
{
    Task SendAsync<TCommand>(TCommand command, CancellationToken cancellationToken = default)
        where TCommand : ICommand;
    Task<TResult> SendAsync<TCommand, TResult>(
        TCommand command,
        CancellationToken cancellationToken = default
    )
        where TCommand : ICommand<TResult>;
    Task<TResult> QueryAsync<TQuery, TResult>(
        TQuery query,
        CancellationToken cancellationToken = default
    )
        where TQuery : IQuery<TResult>;
}
