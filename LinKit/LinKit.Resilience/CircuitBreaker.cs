using System;
using System.Collections.Concurrent;
using LinKit.Core.Resilience;
using Microsoft.Extensions.DependencyInjection;

namespace LinKit.Resilience;

public enum CircuitState
{
    Closed,
    Open,
    HalfOpen
}

public class CircuitBreakerState
{
    private readonly object _lock = new();
    private readonly TimeProvider _time;
    private int _consecutiveFailures;
    private long _openedAtTimestamp;
    private long _probeStartedTimestamp;
    private bool _halfOpenProbeInFlight;
    private long _generation;

    public CircuitBreakerState(TimeProvider? timeProvider = null) => _time = timeProvider ?? TimeProvider.System;

    public CircuitState State { get; private set; } = CircuitState.Closed;

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

public static class CircuitBreakerRegistry
{
    private static readonly ConcurrentDictionary<string, CircuitBreakerState> _breakers = new();

    public static CircuitBreakerState GetOrCreate(string key) => GetOrCreate(key, TimeProvider.System);

    /// <remarks>
    /// The breaker is shared per key; the first caller creating it fixes its <see cref="TimeProvider"/>.
    /// Later callers passing a different provider receive the existing breaker unchanged.
    /// </remarks>
    public static CircuitBreakerState GetOrCreate(string key, TimeProvider timeProvider) =>
        _breakers.TryGetValue(key, out var existing)
            ? existing
            : _breakers.GetOrAdd(key, static (_, provider) => new CircuitBreakerState(provider), timeProvider);
}

public static class ResilienceServiceCollectionExtensions
{
    public static IServiceCollection AddLinKitResilience(this IServiceCollection services)
    {
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton(services, TimeProvider.System);
        return services;
    }
}
