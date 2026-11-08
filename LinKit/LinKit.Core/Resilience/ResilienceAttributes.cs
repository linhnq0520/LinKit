using System;

namespace LinKit.Core.Resilience;

/// <summary>
/// Backoff strategy for <c>LinKit.Resilience.ResilienceOptions.Backoff</c>.
/// </summary>
public enum BackoffType
{
    /// <summary>
    /// The same delay between every attempt. Predictable, and the right choice when the dependency recovers quickly.
    /// </summary>
    Constant,

    /// <summary>
    /// The delay grows linearly with the attempt number, for example 1s, 2s, 3s.
    /// </summary>
    Linear,

    /// <summary>
    /// The delay doubles with every attempt, for example 1s, 2s, 4s, 8s. Best for protecting a saturated dependency,
    /// but the worst case latency is the highest.
    /// </summary>
    Exponential,
}

/// <summary>
/// Thrown when an operation is rejected because its circuit is open.
/// </summary>
public class CircuitBreakerOpenException : Exception
{
    /// <summary>
    /// Initializes the exception.
    /// </summary>
    /// <param name="message">Description of the rejection, usually naming the guarded operation.</param>
    public CircuitBreakerOpenException(string message)
        : base(message) { }
}
