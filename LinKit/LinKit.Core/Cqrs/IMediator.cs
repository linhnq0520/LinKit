using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LinKit.Core.Cqrs;

public interface IMediator
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    Task DispatchAsync(ICommand command, CancellationToken cancellationToken = default);

    [EditorBrowsable(EditorBrowsableState.Never)]
    Task<TResponse> DispatchAsync<TResponse>(
        ICommand<TResponse> command,
        CancellationToken cancellationToken = default
    );

    [EditorBrowsable(EditorBrowsableState.Never)]
    Task<TResponse> DispatchQueryAsync<TResponse>(
        IQuery<TResponse> query,
        CancellationToken cancellationToken = default
    );

    [EditorBrowsable(EditorBrowsableState.Never)]
    Task DispatchNotificationAsync<TNotification>(
        TNotification notification,
        PublishStrategy strategy = PublishStrategy.Sequential,
        CancellationToken ct = default
    )
        where TNotification : INotification;
}

public static class MediatorFallbackExtensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Task SendAsync(
        this IMediator mediator,
        ICommand command,
        CancellationToken cancellationToken = default
    ) => mediator.DispatchAsync(command, cancellationToken);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Task<TResponse> SendAsync<TResponse>(
        this IMediator mediator,
        ICommand<TResponse> command,
        CancellationToken cancellationToken = default
    ) => mediator.DispatchAsync(command, cancellationToken);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Task<TResponse> QueryAsync<TResponse>(
        this IMediator mediator,
        IQuery<TResponse> query,
        CancellationToken cancellationToken = default
    ) => mediator.DispatchQueryAsync(query, cancellationToken);

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

public enum PublishStrategy
{
    Sequential,
    Parallel,
}
