using System;

namespace LinKit.Outbox;

/// <summary>
/// Tuning knobs for <see cref="OutboxPublisherService"/> and the stores it drains. All timing values are interpreted
/// against the injected <see cref="TimeProvider"/>, so tests can advance a fake clock instead of waiting.
/// </summary>
/// <remarks>
/// Call <see cref="Validate"/> after binding configuration so an invalid deployment fails fast at startup rather than
/// on the first dispatch loop.
/// </remarks>
public sealed class OutboxOptions
{
    /// <summary>
    /// Idle delay between polling loops. The delay is skipped when a poll returns a full batch, so throughput is not
    /// throttled while a backlog exists.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Maximum number of messages claimed per poll; also the point at which the idle delay is skipped.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>Attempts before a message is dead-lettered (no longer returned by the store).</summary>
    public int MaxRetries { get; set; } = 5;

    /// <summary>How long a claimed message is invisible to other publisher instances.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>First backoff step; each subsequent failure doubles the delay up to <see cref="MaxRetryDelay"/>.</summary>
    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Upper bound for the exponential backoff between attempts.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Ensures the settings are usable. Called automatically by <see cref="InMemoryOutboxStore"/> and
    /// <see cref="OutboxPublisherService"/>, and by <c>AddLinKitOutbox</c> during registration.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A timing or count value is outside its supported range.</exception>
    public void Validate()
    {
        if (PollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(PollInterval),
                "Poll interval must be greater than zero."
            );
        if (BatchSize <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(BatchSize),
                "Batch size must be greater than zero."
            );
        if (MaxRetries <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(MaxRetries),
                "Max retries must be greater than zero."
            );
        if (LeaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(LeaseDuration),
                "Lease duration must be greater than zero."
            );
        if (BaseRetryDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(BaseRetryDelay),
                "Base retry delay cannot be negative."
            );
        if (MaxRetryDelay < BaseRetryDelay)
            throw new ArgumentOutOfRangeException(
                nameof(MaxRetryDelay),
                "Max retry delay cannot be less than the base retry delay."
            );
    }

    /// <summary>Exponential backoff with cap: base * 2^(attempt-1).</summary>
    /// <param name="attempt">1-based attempt number that just failed; non-positive values are treated as attempt 1.</param>
    /// <returns>
    /// The delay before the next attempt, never greater than <see cref="MaxRetryDelay"/>, or <see cref="TimeSpan.Zero"/>
    /// when <see cref="BaseRetryDelay"/> is zero (immediate retry).
    /// </returns>
    public TimeSpan GetRetryDelay(int attempt)
    {
        var exp = Math.Min(Math.Max(attempt - 1, 0), 20);
        var multiplier = 1L << exp;
        if (BaseRetryDelay == TimeSpan.Zero)
            return TimeSpan.Zero;
        if (BaseRetryDelay.Ticks > MaxRetryDelay.Ticks / multiplier)
            return MaxRetryDelay;

        return TimeSpan.FromTicks(Math.Min(BaseRetryDelay.Ticks * multiplier, MaxRetryDelay.Ticks));
    }
}
