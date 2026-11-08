using System;
using System.Threading;
using System.Threading.Tasks;

namespace LinKit.Data;

/// <summary>
/// Wraps an idempotent database operation with cross-cutting policy (retry, circuit breaker, per-attempt timeout).
/// </summary>
/// <remarks>
/// LinKit.Data deliberately does not reference LinKit.Resilience, so this interface is the seam: an application
/// that references both packages composes them with a few lines.
/// </remarks>
/// <example>
/// <code>
/// public sealed class ResilienceDataExecutor(ResilienceOptions options) : IDataOperationExecutor
/// {
///     public Task&lt;T&gt; ExecuteAsync&lt;T&gt;(Func&lt;CancellationToken, Task&lt;T&gt;&gt; operation, CancellationToken ct) =&gt;
///         ResiliencePipeline.ExecuteAsync("linkit-data", operation, options, ct);
/// }
/// </code>
/// </example>
public interface IDataOperationExecutor
{
    /// <summary>Runs an idempotent operation under the configured policy.</summary>
    /// <typeparam name="T">Result type.</typeparam>
    /// <param name="operation">Work to run; it may be invoked more than once, so it must be safe to repeat.</param>
    /// <param name="cancellationToken">Token used to cancel the operation and the waits between attempts.</param>
    /// <returns>The value produced by the successful attempt.</returns>
    Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Build-time-free retry for transient database failures (deadlocks, dropped connections, transient throttling).
/// Delays grow exponentially with a jitter so a fleet of workers does not retry in lockstep.
/// </summary>
public sealed class TransientFailureExecutor : IDataOperationExecutor
{
    private readonly Func<Exception, bool> _isTransient;
    private readonly int _maxRetries;
    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan _maxDelay;
    private readonly TimeProvider _time;
    private readonly Func<int, TimeSpan>? _delayFactory;

    /// <summary>Creates a retry executor.</summary>
    /// <param name="isTransient">Predicate that decides whether an exception is worth retrying.</param>
    /// <param name="maxRetries">Maximum number of additional attempts; zero disables retrying.</param>
    /// <param name="baseDelay">Delay before the first retry, doubled on each following attempt; defaults to 100 ms.</param>
    /// <param name="maxDelay">Upper bound for a single delay; defaults to 10 seconds.</param>
    /// <param name="timeProvider">Clock used for the delays, so tests can run without waiting.</param>
    /// <param name="delayFactory">Overrides the exponential backoff with a custom delay per attempt (one-based).</param>
    /// <exception cref="ArgumentNullException"><paramref name="isTransient"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxRetries"/> is negative.</exception>
    public TransientFailureExecutor(
        Func<Exception, bool> isTransient,
        int maxRetries = 3,
        TimeSpan? baseDelay = null,
        TimeSpan? maxDelay = null,
        TimeProvider? timeProvider = null,
        Func<int, TimeSpan>? delayFactory = null
    )
    {
        _isTransient = isTransient ?? throw new ArgumentNullException(nameof(isTransient));
        if (maxRetries < 0)
            throw new ArgumentOutOfRangeException(
                nameof(maxRetries),
                "Max retries cannot be negative."
            );

        _maxRetries = maxRetries;
        _baseDelay = baseDelay ?? TimeSpan.FromMilliseconds(100);
        _maxDelay = maxDelay ?? TimeSpan.FromSeconds(10);
        _time = timeProvider ?? TimeProvider.System;
        _delayFactory = delayFactory;
    }

    /// <summary>Runs the operation, retrying transient failures until the attempts are exhausted.</summary>
    /// <typeparam name="T">Result type.</typeparam>
    /// <param name="operation">Work to run; it may be invoked more than once, so it must be safe to repeat.</param>
    /// <param name="cancellationToken">Token used to cancel the operation and the waits between attempts.</param>
    /// <returns>The value produced by the successful attempt.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is cancelled.</exception>
    /// <remarks>The last failure is rethrown when the attempts are exhausted or the failure is not transient.</remarks>
    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(operation);

        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error) when (attempt < _maxRetries && _isTransient(error))
            {
                await Task.Delay(ComputeDelay(attempt + 1), _time, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private TimeSpan ComputeDelay(int attempt)
    {
        if (_delayFactory is not null)
        {
            return _delayFactory(attempt);
        }

        double factor = Math.Pow(2, Math.Min(attempt - 1, 20));
        double jitter = 1 + (Random.Shared.NextDouble() * 0.2 - 0.1);
        var milliseconds = Math.Min(
            _baseDelay.TotalMilliseconds * factor * jitter,
            _maxDelay.TotalMilliseconds
        );
        return TimeSpan.FromMilliseconds(milliseconds);
    }
}
