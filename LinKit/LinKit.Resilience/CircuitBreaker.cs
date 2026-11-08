using System;
using System.Collections.Concurrent;
using LinKit.Core.Resilience;
using Microsoft.Extensions.DependencyInjection;

namespace LinKit.Resilience;

/// <summary>Lifecycle state of a <see cref="CircuitBreakerState"/>.</summary>
public enum CircuitState
{
    /// <summary>Requests flow normally; consecutive failures are counted.</summary>
    Closed,

    /// <summary>Requests are rejected immediately until the break duration elapses.</summary>
    Open,

    /// <summary>A single probe is admitted to decide whether the breaker can close again.</summary>
    HalfOpen,
}

/// <summary>
/// Thread-safe circuit breaker with a generation counter that rejects stale outcomes.
/// </summary>
/// <remarks>
/// A slow success admitted while the breaker was closed cannot close a breaker that a later failure opened: the
/// generation captured at admission time is compared on completion, so only an outcome from the current generation
/// affects the state. A half-open probe that never reports back cannot wedge the breaker either; once the break
/// duration elapses a new probe is admitted.
/// </remarks>
public class CircuitBreakerState
{
    private readonly object _lock = new();
    private readonly TimeProvider _time;
    private int _consecutiveFailures;
    private long _openedAtTimestamp;
    private long _probeStartedTimestamp;
    private bool _halfOpenProbeInFlight;
    private long _generation;

    /// <summary>Creates a closed breaker bound to the supplied clock.</summary>
    /// <param name="timeProvider">Clock used for break and probe durations; defaults to <see cref="TimeProvider.System"/>.</param>
    public CircuitBreakerState(TimeProvider? timeProvider = null) =>
        _time = timeProvider ?? TimeProvider.System;

    /// <summary>The current breaker state.</summary>
    public CircuitState State { get; private set; } = CircuitState.Closed;

    /// <summary>Determines whether an operation may run, without reporting the generation.</summary>
    /// <param name="durationSeconds">Length of the open window before a probe is admitted.</param>
    /// <returns><see langword="true"/> when the operation may proceed.</returns>
    public bool CanExecute(int durationSeconds) => CanExecute(durationSeconds, out _);

    /// <summary>
    /// Admits an operation and captures the state generation it was admitted under.
    /// Pass that generation to <see cref="OnSuccess(long)"/> so a stale success admitted
    /// before a later transition cannot close the breaker.
    /// </summary>
    public bool CanExecute(int durationSeconds, out long generation)
    {
        lock (_lock)
        {
            var duration = TimeSpan.FromSeconds(durationSeconds);
            if (State == CircuitState.Closed)
            {
                generation = _generation;
                return true;
            }

            if (State == CircuitState.Open)
            {
                if (_time.GetElapsedTime(_openedAtTimestamp) >= duration)
                {
                    State = CircuitState.HalfOpen;
                    _generation++;
                    _halfOpenProbeInFlight = true;
                    _probeStartedTimestamp = _time.GetTimestamp();
                    generation = _generation;
                    return true;
                }
                generation = _generation;
                return false;
            }

            // HalfOpen: exactly one probe, but a probe that never reported back must not wedge the breaker forever.
            if (!_halfOpenProbeInFlight || _time.GetElapsedTime(_probeStartedTimestamp) >= duration)
            {
                // A newly admitted probe invalidates any earlier probe that never reported back.
                _generation++;
                _halfOpenProbeInFlight = true;
                _probeStartedTimestamp = _time.GetTimestamp();
                generation = _generation;
                return true;
            }
            generation = _generation;
            return false;
        }
    }

    /// <summary>Reports a successful operation that was admitted without a generation.</summary>
    public void OnSuccess() => OnSuccessCore(null);

    /// <summary>
    /// Reports success for an operation admitted under <paramref name="generation"/>
    /// (see <see cref="CanExecute(int, out long)"/>). Stale successes whose generation no
    /// longer matches are ignored, so a slow success admitted while Closed cannot close a
    /// breaker that a later failure opened; only a matching HalfOpen probe closes it.
    /// </summary>
    public void OnSuccess(long generation) => OnSuccessCore(generation);

    private void OnSuccessCore(long? generation)
    {
        lock (_lock)
        {
            if (generation.HasValue && generation.Value != _generation)
                return;

            _consecutiveFailures = 0;
            _halfOpenProbeInFlight = false;
            if (State != CircuitState.Closed)
            {
                // A matching generation implies HalfOpen: every transition to Open bumps the generation.
                State = CircuitState.Closed;
                _generation++;
            }
        }
    }

    /// <summary>Records a failed attempt and opens the breaker once <paramref name="threshold"/> is reached.</summary>
    /// <param name="threshold">Number of consecutive failures that trips the breaker; a failure during a half-open
    /// probe opens it immediately.</param>
    public void OnFailure(int threshold)
    {
        lock (_lock)
        {
            _consecutiveFailures++;
            _halfOpenProbeInFlight = false;
            if (_consecutiveFailures >= threshold || State == CircuitState.HalfOpen)
            {
                State = CircuitState.Open;
                _generation++;
                _openedAtTimestamp = _time.GetTimestamp();
            }
        }
    }
}

/// <summary>
/// Process-wide registry of named circuit breakers, so every call site using the same key shares one breaker.
/// </summary>
public static class CircuitBreakerRegistry
{
    private static readonly ConcurrentDictionary<string, CircuitBreakerState> _breakers = new();

    /// <summary>Returns the breaker for <paramref name="key"/>, creating it against the system clock if needed.</summary>
    /// <param name="key">Logical breaker name, usually the dependency being protected.</param>
    /// <returns>The shared breaker instance.</returns>
    public static CircuitBreakerState GetOrCreate(string key) =>
        GetOrCreate(key, TimeProvider.System);

    /// <summary>Returns the breaker for <paramref name="key"/>, creating it if needed.</summary>
    /// <param name="key">Logical breaker name, usually the dependency being protected.</param>
    /// <param name="timeProvider">Clock used when this call creates the breaker.</param>
    /// <returns>The shared breaker instance.</returns>
    /// <remarks>
    /// The breaker is shared per key; the first caller creating it fixes its <see cref="TimeProvider"/>.
    /// Later callers passing a different provider receive the existing breaker unchanged.
    /// </remarks>
    public static CircuitBreakerState GetOrCreate(string key, TimeProvider timeProvider) =>
        _breakers.TryGetValue(key, out var existing)
            ? existing
            : _breakers.GetOrAdd(
                key,
                static (_, provider) => new CircuitBreakerState(provider),
                timeProvider
            );
}

/// <summary>
/// Registration helpers for the resilience primitives.
/// </summary>
public static class ResilienceServiceCollectionExtensions
{
    /// <summary>
    /// Registers the shared <see cref="TimeProvider"/> used by breakers and retry delays. Call this once during host
    /// setup; an existing registration (for example a test fake clock) is preserved.
    /// </summary>
    /// <param name="services">Service collection to configure.</param>
    /// <returns>The same collection, to allow call chaining.</returns>
    public static IServiceCollection AddLinKitResilience(this IServiceCollection services)
    {
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton(
            services,
            TimeProvider.System
        );
        return services;
    }
}
