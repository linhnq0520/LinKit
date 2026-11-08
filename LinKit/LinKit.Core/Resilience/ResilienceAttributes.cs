using System;

namespace LinKit.Core.Resilience;

/// <summary>
/// Backoff strategy for <c>LinKit.Resilience.ResilienceOptions.Backoff</c>.
/// </summary>
public enum BackoffType
{
    Constant,
    Linear,
    Exponential,
}

/// <summary>
/// Thrown when an operation is rejected because its circuit is open.
/// </summary>
public class CircuitBreakerOpenException : Exception
{
    public CircuitBreakerOpenException(string message)
        : base(message) { }
}
