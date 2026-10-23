using System;

namespace LinKit.Core.Resilience;

public enum BackoffType
{
    Constant,
    Linear,
    Exponential
}

/// <summary>
/// Declares retry policy metadata.
/// This attribute is currently metadata only; use <see cref="ResiliencePipeline"/> for retry behavior.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Interface, Inherited = true, AllowMultiple = false)]
public sealed class RetryAttribute : Attribute
{
    public int MaxRetries { get; set; } = 3;
    public int DelayMilliseconds { get; set; } = 200;
    public BackoffType Backoff { get; set; } = BackoffType.Exponential;

    public RetryAttribute(int maxRetries = 3, int delayMilliseconds = 200, BackoffType backoff = BackoffType.Exponential)
    {
        MaxRetries = maxRetries;
        DelayMilliseconds = delayMilliseconds;
        Backoff = backoff;
    }
}

/// <summary>
/// Declares circuit-breaker policy metadata.
/// This attribute is currently metadata only; use <see cref="ResiliencePipeline"/> for circuit-breaker behavior.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Interface, Inherited = true, AllowMultiple = false)]
public sealed class CircuitBreakerAttribute : Attribute
{
    public int FailureThreshold { get; set; } = 5;
    public int DurationSeconds { get; set; } = 30;
    public int MinimumThroughput { get; set; } = 5;

    public CircuitBreakerAttribute(int failureThreshold = 5, int durationSeconds = 30, int minimumThroughput = 5)
    {
        FailureThreshold = failureThreshold;
        DurationSeconds = durationSeconds;
        MinimumThroughput = minimumThroughput;
    }
}

/// <summary>
/// Declares timeout policy metadata.
/// This attribute is currently metadata only; use <see cref="ResiliencePipeline"/> for timeout behavior.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Interface, Inherited = true, AllowMultiple = false)]
public sealed class TimeoutAttribute : Attribute
{
    public int Milliseconds { get; set; } = 5000;

    public TimeoutAttribute(int milliseconds = 5000)
    {
        Milliseconds = milliseconds;
    }
}

public class CircuitBreakerOpenException : Exception
{
    public CircuitBreakerOpenException(string message) : base(message) { }
}
