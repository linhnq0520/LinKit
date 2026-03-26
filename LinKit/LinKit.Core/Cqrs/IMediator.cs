namespace LinKit.Core.Cqrs;

public interface IMediator
{
    Task SendAsync(ICommand command, CancellationToken cancellationToken = default);

    Task<TResponse> SendAsync<TResponse>(
        ICommand<TResponse> command,
        CancellationToken cancellationToken = default
    );

    Task<TResponse> QueryAsync<TResponse>(
        IQuery<TResponse> query,
        CancellationToken cancellationToken = default
    );

    Task PublishAsync<TNotification>(
        TNotification notification,
        PublishStrategy strategy = PublishStrategy.Sequential,
        CancellationToken ct = default
    );
}

public enum PublishStrategy
{
    Sequential,
    Parallel,
}
