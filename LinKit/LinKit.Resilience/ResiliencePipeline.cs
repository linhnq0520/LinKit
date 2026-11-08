using System;
using System.Threading;
using System.Threading.Tasks;
using LinKit.Core.Resilience;

namespace LinKit.Resilience;

/// <summary>
/// Retry, circuit-breaker and per-attempt timeout settings consumed by <see cref="ResiliencePipeline"/>.
/// </summary>
public sealed class ResilienceOptions
{
    /// <summary>Additional attempts after the first failure; 0 disables retries.</summary>
    public int MaxRetries { get; set; }

    /// <summary>Base delay between attempts, before backoff and jitter are applied.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>How <see cref="RetryDelay"/> grows with each attempt.</summary>
    public BackoffType Backoff { get; set; } = BackoffType.Exponential;

    /// <summary>Upper bound for a single retry delay, including the +/-10% jitter.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Per-attempt timeout. Null disables.</summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>Circuit breaker failure threshold; 0 disables the breaker.</summary>
    public int BreakerFailureThreshold { get; set; }

    /// <summary>Open window after the breaker trips, in seconds.</summary>
    public int BreakerDurationSeconds { get; set; } = 30;

    /// <summary>Decides if an exception is transient. Default: everything except cancellation and open circuits.</summary>
    public Func<Exception, bool>? ShouldRetry { get; set; }

    /// <summary>Throws when a value is outside its supported range.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A retry, timeout or breaker value is invalid.</exception>
    public void Validate()
    {
        if (MaxRetries < 0)
            throw new ArgumentOutOfRangeException(
                nameof(MaxRetries),
                "Max retries cannot be negative."
            );
        if (RetryDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(RetryDelay),
                "Retry delay cannot be negative."
            );
        if (MaxRetryDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(MaxRetryDelay),
                "Maximum retry delay cannot be negative."
            );
        if (Timeout is { } timeout && timeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(Timeout), "Timeout cannot be negative.");
        if (BreakerFailureThreshold < 0)
            throw new ArgumentOutOfRangeException(
                nameof(BreakerFailureThreshold),
                "Breaker failure threshold cannot be negative."
            );
        if (BreakerFailureThreshold > 0 && BreakerDurationSeconds <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(BreakerDurationSeconds),
                "Breaker duration must be greater than zero when the breaker is enabled."
            );
    }
}

/// <summary>
/// Allocation-light, reflection-free resilience executor. Order: Retry (outer) -> CircuitBreaker -> Timeout -> action.
/// </summary>
public static class ResiliencePipeline
{
    /// <summary>
    /// Executes <paramref name="action"/> with retries, an optional circuit breaker and an optional per-attempt timeout.
    /// </summary>
    /// <typeparam name="T">Result type produced by the action.</typeparam>
    /// <param name="key">Breaker identity; call sites protecting the same dependency should share a key.</param>
    /// <param name="action">Operation to execute; it receives the token that aborts on caller cancellation or timeout.</param>
    /// <param name="options">Retry, breaker and timeout settings; validated on every call.</param>
    /// <param name="cancellationToken">Token used to abort the whole pipeline.</param>
    /// <param name="timeProvider">Clock used for delays and the breaker; defaults to <see cref="TimeProvider.System"/>.</param>
    /// <returns>The result of the first successful attempt.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="action"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="CircuitBreakerOpenException">The breaker is open and rejected the call without executing it.</exception>
    /// <exception cref="TimeoutException">Every attempt exceeded <see cref="ResilienceOptions.Timeout"/>.</exception>
    public static async Task<T> ExecuteAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> action,
        ResilienceOptions options,
        CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var time = timeProvider ?? TimeProvider.System;
        var breaker =
            options.BreakerFailureThreshold > 0
                ? CircuitBreakerRegistry.GetOrCreate(key, time)
                : null;

        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long generation = 0;
            if (
                breaker != null
                && !breaker.CanExecute(options.BreakerDurationSeconds, out generation)
            )
                throw new CircuitBreakerOpenException($"Circuit '{key}' is open.");

            try
            {
                var result = await RunWithTimeoutAsync(
                        action,
                        options.Timeout,
                        time,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                breaker?.OnSuccess(generation);
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                breaker?.OnFailure(options.BreakerFailureThreshold);

                bool retryable =
                    ex is not CircuitBreakerOpenException
                    && (options.ShouldRetry?.Invoke(ex) ?? ex is not OperationCanceledException);
                if (!retryable || attempt >= options.MaxRetries)
                    throw;

                await Task.Delay(ComputeDelay(options, attempt), time, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>Executes a result-less <paramref name="action"/> with the same retry, breaker and timeout policy.</summary>
    /// <param name="key">Breaker identity; call sites protecting the same dependency should share a key.</param>
    /// <param name="action">Operation to execute.</param>
    /// <param name="options">Retry, breaker and timeout settings; validated on every call.</param>
    /// <param name="cancellationToken">Token used to abort the whole pipeline.</param>
    /// <param name="timeProvider">Clock used for delays and the breaker; defaults to <see cref="TimeProvider.System"/>.</param>
    public static async Task ExecuteAsync(
        string key,
        Func<CancellationToken, Task> action,
        ResilienceOptions options,
        CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null
    )
    {
        await ExecuteAsync<bool>(
                key,
                async ct =>
                {
                    await action(ct).ConfigureAwait(false);
                    return true;
                },
                options,
                cancellationToken,
                timeProvider
            )
            .ConfigureAwait(false);
    }

    private static async Task<T> RunWithTimeoutAsync<T>(
        Func<CancellationToken, Task<T>> action,
        TimeSpan? timeout,
        TimeProvider time,
        CancellationToken ct
    )
    {
        if (timeout is null)
            return await action(ct).ConfigureAwait(false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var timer = time.CreateTimer(
            static s => ((CancellationTokenSource)s!).Cancel(),
            cts,
            timeout.Value,
            System.Threading.Timeout.InfiniteTimeSpan
        );
        try
        {
            return await action(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (!ct.IsCancellationRequested && cts.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Operation timed out after {timeout.Value.TotalMilliseconds:0} ms."
            );
        }
    }

    internal static TimeSpan ComputeDelay(ResilienceOptions o, int attempt)
    {
        double factor = o.Backoff switch
        {
            BackoffType.Constant => 1,
            BackoffType.Linear => attempt + 1,
            _ => Math.Pow(2, Math.Min(attempt, 20)),
        };
        var jitter = 1 + (Random.Shared.NextDouble() * 0.2 - 0.1); // +/-10% to avoid thundering herd
        var ms = Math.Min(
            o.RetryDelay.TotalMilliseconds * factor * jitter,
            o.MaxRetryDelay.TotalMilliseconds
        );
        return TimeSpan.FromMilliseconds(ms);
    }
}
