namespace LinKit.Core.Cqrs;

public interface IRequest { }

public interface IRequest<out TResponse> : IRequest { }

public interface ICommand : ICommand<Unit> { }

public interface ICommand<out TResponse> : IRequest<TResponse> { }

public interface IQuery<out TResponse> : IRequest<TResponse> { }

public interface INotification { }

public readonly struct Unit
{
    public static readonly Unit Value = new();
}

/// <summary>
public interface IHandler<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> HandleAsync(TRequest request, CancellationToken cancellationToken = default);
}

public interface ICommandHandler<in TCommand> : IHandler<TCommand, Unit>
    where TCommand : ICommand { }

public interface ICommandHandler<in TCommand, TResponse> : IHandler<TCommand, TResponse>
    where TCommand : ICommand<TResponse> { }

public interface IQueryHandler<in TQuery, TResponse> : IHandler<TQuery, TResponse>
    where TQuery : IQuery<TResponse> { }

public delegate Task<TResponse> RequestHandlerDelegate<TResponse>();

public interface IPipelineBehavior<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> HandleAsync(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken
    );
}

public interface INotificationHandler<in TNotification>
    where TNotification : INotification
{
    Task HandleAsync(TNotification notification, CancellationToken cancellationToken = default);
}
