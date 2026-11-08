using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LinKit.Core.Cqrs;

/// <summary>
/// Dispatches requests to their handlers. The implementation is generated at compile time, so dispatch is a direct
/// call with no reflection and no runtime type scanning, which keeps it Native AOT safe.
/// </summary>
/// <remarks>
/// The <c>Dispatch*</c> members are generated entry points and are hidden from IntelliSense. Application code should
/// call the <see cref="MediatorFallbackExtensions.SendAsync(IMediator, ICommand, CancellationToken)"/>,
/// <see cref="MediatorFallbackExtensions.QueryAsync{TResponse}"/> and
/// <see cref="MediatorFallbackExtensions.PublishAsync{TNotification}"/> extensions instead.
/// </remarks>
public interface IMediator
{
    /// <summary>
    /// Sends a command that has no payload to its handler. Prefer the
    /// <see cref="MediatorFallbackExtensions.SendAsync(IMediator, ICommand, CancellationToken)"/> extension.
    /// </summary>
    /// <param name="command">Command to dispatch.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>A task that completes when the handler finishes.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    Task DispatchAsync(ICommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a command that produces a response to its handler. Prefer the
    /// <see cref="MediatorFallbackExtensions.SendAsync{TResponse}"/> extension.
    /// </summary>
    /// <typeparam name="TResponse">Response type produced by the handler.</typeparam>
    /// <param name="command">Command to dispatch.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>The response produced by the handler.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    Task<TResponse> DispatchAsync<TResponse>(
        ICommand<TResponse> command,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Sends a query to its handler. Prefer the <see cref="MediatorFallbackExtensions.QueryAsync{TResponse}"/> extension.
    /// </summary>
    /// <typeparam name="TResponse">Response type produced by the handler.</typeparam>
    /// <param name="query">Query to dispatch.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>The response produced by the handler.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    Task<TResponse> DispatchQueryAsync<TResponse>(
        IQuery<TResponse> query,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Publishes a notification to every registered handler. Prefer the
    /// <see cref="MediatorFallbackExtensions.PublishAsync{TNotification}"/> extension.
    /// </summary>
    /// <typeparam name="TNotification">Notification type being published.</typeparam>
    /// <param name="notification">Notification to publish.</param>
    /// <param name="strategy">Whether handlers run one after another or concurrently.</param>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>A task that completes when every handler has finished.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    Task DispatchNotificationAsync<TNotification>(
        TNotification notification,
        PublishStrategy strategy = PublishStrategy.Sequential,
        CancellationToken ct = default
    )
        where TNotification : INotification;
}

/// <summary>
/// Conventional mediator API layered on top of the generated <see cref="IMediator"/> members, so application code reads
/// as <c>mediator.SendAsync(command)</c> instead of <c>mediator.DispatchAsync(command)</c>.
/// </summary>
public static class MediatorFallbackExtensions
{
    /// <summary>
    /// Sends a command that has no payload.
    /// </summary>
    /// <param name="mediator">Mediator resolved from the container.</param>
    /// <param name="command">Command to send.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>A task that completes when the handler finishes.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Task SendAsync(
        this IMediator mediator,
        ICommand command,
        CancellationToken cancellationToken = default
    ) => mediator.DispatchAsync(command, cancellationToken);

    /// <summary>
    /// Sends a command that produces a response.
    /// </summary>
    /// <typeparam name="TResponse">Response type produced by the handler.</typeparam>
    /// <param name="mediator">Mediator resolved from the container.</param>
    /// <param name="command">Command to send.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>The response produced by the handler.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Task<TResponse> SendAsync<TResponse>(
        this IMediator mediator,
        ICommand<TResponse> command,
        CancellationToken cancellationToken = default
    ) => mediator.DispatchAsync(command, cancellationToken);

    /// <summary>
    /// Sends a query.
    /// </summary>
    /// <typeparam name="TResponse">Response type produced by the handler.</typeparam>
    /// <param name="mediator">Mediator resolved from the container.</param>
    /// <param name="query">Query to send.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>The response produced by the handler.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Task<TResponse> QueryAsync<TResponse>(
        this IMediator mediator,
        IQuery<TResponse> query,
        CancellationToken cancellationToken = default
    ) => mediator.DispatchQueryAsync(query, cancellationToken);

    /// <summary>
    /// Publishes a notification to every registered handler.
    /// </summary>
    /// <typeparam name="TNotification">Notification type being published.</typeparam>
    /// <param name="mediator">Mediator resolved from the container.</param>
    /// <param name="notification">Notification to publish.</param>
    /// <param name="strategy">Whether handlers run one after another or concurrently.</param>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>A task that completes when every handler has finished.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Task PublishAsync<TNotification>(
        this IMediator mediator,
        TNotification notification,
        PublishStrategy strategy = PublishStrategy.Sequential,
        CancellationToken ct = default
    )
        where TNotification : INotification =>
        mediator.DispatchNotificationAsync(notification, strategy, ct);
}

/// <summary>
/// How a notification is delivered to its handlers.
/// </summary>
public enum PublishStrategy
{
    /// <summary>
    /// Handlers run one at a time in registration order. Deterministic and easiest to debug, but the slowest
    /// handler decides the total latency.
    /// </summary>
    Sequential,

    /// <summary>
    /// Handlers run concurrently and the publish completes when all of them finish. Fastest when handlers are I/O
    /// bound, but ordering between handlers is not guaranteed.
    /// </summary>
    Parallel,
}
