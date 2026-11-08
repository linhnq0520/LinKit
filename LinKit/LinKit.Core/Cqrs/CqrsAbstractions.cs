namespace LinKit.Core.Cqrs;

/// <summary>
/// Marker for anything the mediator can dispatch. Requests are handled by exactly one handler, so dispatching a
/// request without a registered handler throws <see cref="HandlerNotFoundException"/>.
/// </summary>
public interface IRequest { }

/// <summary>
/// A request that produces a response of type <typeparamref name="TResponse"/>.
/// </summary>
/// <typeparam name="TResponse">Response type produced by the handler.</typeparam>
public interface IRequest<out TResponse> : IRequest { }

/// <summary>
/// A state-changing request that produces no payload. The handler returns <see cref="Unit"/>.
/// </summary>
public interface ICommand : ICommand<Unit> { }

/// <summary>
/// A state-changing request that produces a response of type <typeparamref name="TResponse"/>.
/// </summary>
/// <typeparam name="TResponse">Response type produced by the handler.</typeparam>
public interface ICommand<out TResponse> : IRequest<TResponse> { }

/// <summary>
/// A read-only request that produces a response of type <typeparamref name="TResponse"/>.
/// </summary>
/// <typeparam name="TResponse">Response type produced by the handler.</typeparam>
public interface IQuery<out TResponse> : IRequest<TResponse> { }

/// <summary>
/// A one-to-many notification. Unlike requests, notifications are delivered to every registered handler and are not
/// expected to return a value.
/// </summary>
public interface INotification { }

/// <summary>
/// Implemented by a CQRS request that carries a caller-side deadline. In-process dispatch ignores it; generated
/// gRPC clients translate it into <c>CallOptions.Deadline</c> so a slow remote call cannot outlive the request.
/// </summary>
public interface IHasDeadline
{
    /// <summary>
    /// How long the call may run, relative to the moment it is issued. A <see langword="null"/> or non-positive value
    /// means no deadline is applied.
    /// </summary>
    TimeSpan? Deadline { get; }
}

/// <summary>
/// The absence of a value, used as the response type of <see cref="ICommand"/> so command handlers stay generic.
/// </summary>
public readonly struct Unit
{
    /// <summary>
    /// The single shared instance of <see cref="Unit"/>.
    /// </summary>
    public static readonly Unit Value = new();
}

/// <summary>
/// Handles a request of type <typeparamref name="TRequest"/> and produces <typeparamref name="TResponse"/>.
/// </summary>
/// <typeparam name="TRequest">Request type being handled.</typeparam>
/// <typeparam name="TResponse">Response type produced by the handler.</typeparam>
public interface IHandler<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    /// <summary>
    /// Handles <paramref name="request"/>.
    /// </summary>
    /// <param name="request">The request to handle.</param>
    /// <param name="cancellationToken">Token that cancels the operation when the caller disconnects or the host stops.</param>
    /// <returns>The response produced for the request.</returns>
    Task<TResponse> HandleAsync(TRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Handles a command that produces no payload.
/// </summary>
/// <typeparam name="TCommand">Command type being handled.</typeparam>
public interface ICommandHandler<in TCommand> : IHandler<TCommand, Unit>
    where TCommand : ICommand { }

/// <summary>
/// Handles a command that produces a response.
/// </summary>
/// <typeparam name="TCommand">Command type being handled.</typeparam>
/// <typeparam name="TResponse">Response type produced by the handler.</typeparam>
public interface ICommandHandler<in TCommand, TResponse> : IHandler<TCommand, TResponse>
    where TCommand : ICommand<TResponse> { }

/// <summary>
/// Handles a query.
/// </summary>
/// <typeparam name="TQuery">Query type being handled.</typeparam>
/// <typeparam name="TResponse">Response type produced by the handler.</typeparam>
public interface IQueryHandler<in TQuery, TResponse> : IHandler<TQuery, TResponse>
    where TQuery : IQuery<TResponse> { }

/// <summary>
/// Invokes the next step of the pipeline: the following behavior, or the handler itself when this is the last behavior.
/// </summary>
/// <typeparam name="TResponse">Response type produced by the request.</typeparam>
public delegate Task<TResponse> RequestHandlerDelegate<TResponse>();

/// <summary>
/// Cross-cutting step wrapped around every request, such as validation, logging, caching or transaction management.
/// Behaviors run in registration order and may short-circuit by not calling <c>next</c>.
/// </summary>
/// <typeparam name="TRequest">Request type being processed.</typeparam>
/// <typeparam name="TResponse">Response type produced by the request.</typeparam>
public interface IPipelineBehavior<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    /// <summary>
    /// Processes <paramref name="request"/> and optionally calls <paramref name="next"/>.
    /// </summary>
    /// <param name="request">The request being processed.</param>
    /// <param name="next">Delegate that continues the pipeline.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>The response of the request.</returns>
    Task<TResponse> HandleAsync(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Handles a notification. Every handler of a notification runs; a handler that throws does not prevent the others
/// from running unless the publisher is configured to stop on the first failure.
/// </summary>
/// <typeparam name="TNotification">Notification type being handled.</typeparam>
public interface INotificationHandler<in TNotification>
    where TNotification : INotification
{
    /// <summary>
    /// Handles <paramref name="notification"/>.
    /// </summary>
    /// <param name="notification">The notification to handle.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>A task that completes when the notification has been handled.</returns>
    Task HandleAsync(TNotification notification, CancellationToken cancellationToken = default);
}
