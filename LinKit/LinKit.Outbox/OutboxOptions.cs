using System;

namespace LinKit.Outbox;

public sealed class OutboxOptions
{
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    public int BatchSize { get; set; } = 100;

    /// <summary>Attempts before a message is dead-lettered (no longer returned by the store).</summary>
    public int MaxRetries { get; set; } = 5;

    /// <summary>How long a claimed message is invisible to other publisher instances.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(5);

    public void Validate()
    {
        if (PollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(PollInterval), "Poll interval must be greater than zero.");
        if (BatchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(BatchSize), "Batch size must be greater than zero.");
        if (MaxRetries <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxRetries), "Max retries must be greater than zero.");
        if (LeaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(LeaseDuration), "Lease duration must be greater than zero.");
        if (BaseRetryDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(BaseRetryDelay), "Base retry delay cannot be negative.");
        if (MaxRetryDelay < BaseRetryDelay)
            throw new ArgumentOutOfRangeException(nameof(MaxRetryDelay), "Max retry delay cannot be less than the base retry delay.");
    }

    /// <summary>Exponential backoff with cap: base * 2^(attempt-1).</summary>
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
