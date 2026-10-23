using System;
using System.Threading;
using System.Threading.Tasks;
using LinKit.Core.Resilience;

namespace LinKit.Resilience;

public sealed class ResilienceOptions
{
    public int MaxRetries { get; set; }
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(200);
    public BackoffType Backoff { get; set; } = BackoffType.Exponential;
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Per-attempt timeout. Null disables.</summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>Circuit breaker failure threshold; 0 disables the breaker.</summary>
    public int BreakerFailureThreshold { get; set; }
    public int BreakerDurationSeconds { get; set; } = 30;

    /// <summary>Decides if an exception is transient. Default: everything except cancellation and open circuits.</summary>
    public Func<Exception, bool>? ShouldRetry { get; set; }

    public void Validate()
    {
        if (MaxRetries < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxRetries), "Max retries cannot be negative.");
        if (RetryDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(RetryDelay), "Retry delay cannot be negative.");
        if (MaxRetryDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(MaxRetryDelay), "Maximum retry delay cannot be negative.");
        if (Timeout is { } timeout && timeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(Timeout), "Timeout cannot be negative.");
        if (BreakerFailureThreshold < 0)
            throw new ArgumentOutOfRangeException(nameof(BreakerFailureThreshold), "Breaker failure threshold cannot be negative.");
        if (BreakerFailureThreshold > 0 && BreakerDurationSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(BreakerDurationSeconds), "Breaker duration must be greater than zero when the breaker is enabled.");
    }
}

/// <summary>
/// Allocation-light, reflection-free resilience executor. Order: Retry (outer) -> CircuitBreaker -> Timeout -> action.
/// </summary>
public static class ResiliencePipeline
{
    public static async Task<T> ExecuteAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> action,
        ResilienceOptions options,
        CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var time = timeProvider ?? TimeProvider.System;
        var breaker = options.BreakerFailureThreshold > 0 ? CircuitBreakerRegistry.GetOrCreate(key, time) : null;

        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (breaker != null && !breaker.CanExecute(options.BreakerDurationSeconds))
                throw new CircuitBreakerOpenException($"Circuit '{key}' is open.");

            try
            {
                var result = await RunWithTimeoutAsync(action, options.Timeout, time, cancellationToken).ConfigureAwait(false);
                breaker?.OnSuccess();
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                breaker?.OnFailure(options.BreakerFailureThreshold);

                bool retryable = ex is not CircuitBreakerOpenException && (options.ShouldRetry?.Invoke(ex) ?? true);
                if (!retryable || attempt >= options.MaxRetries)
                    throw;

                await Task.Delay(ComputeDelay(options, attempt), time, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public static async Task ExecuteAsync(
        string key,
        Func<CancellationToken, Task> action,
        ResilienceOptions options,
        CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null)
    {
        await ExecuteAsync<bool>(key, async ct => { await action(ct).ConfigureAwait(false); return true; }, options, cancellationToken, timeProvider).ConfigureAwait(false);
    }

    private static async Task<T> RunWithTimeoutAsync<T>(Func<CancellationToken, Task<T>> action, TimeSpan? timeout, TimeProvider time, CancellationToken ct)
    {
        if (timeout is null)
            return await action(ct).ConfigureAwait(false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var timer = time.CreateTimer(static s => ((CancellationTokenSource)s!).Cancel(), cts, timeout.Value, System.Threading.Timeout.InfiniteTimeSpan);
        try
        {
            return await action(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && cts.IsCancellationRequested)
        {
            throw new TimeoutException($"Operation timed out after {timeout.Value.TotalMilliseconds:0} ms.");
        }
    }

    internal static TimeSpan ComputeDelay(ResilienceOptions o, int attempt)
    {
        double factor = o.Backoff switch
        {
            BackoffType.Constant => 1,
            BackoffType.Linear => attempt + 1,
            _ => Math.Pow(2, Math.Min(attempt, 20))
        };
        var jitter = 1 + (Random.Shared.NextDouble() * 0.2 - 0.1); // +/-10% to avoid thundering herd
        var ms = Math.Min(o.RetryDelay.TotalMilliseconds * factor * jitter, o.MaxRetryDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(ms);
    }
}
