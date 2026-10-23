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

    public CircuitBreakerState(TimeProvider? timeProvider = null) => _time = timeProvider ?? TimeProvider.System;

    public CircuitState State { get; private set; } = CircuitState.Closed;

    public bool CanExecute(int durationSeconds)
    {
        lock (_lock)
        {
            var duration = TimeSpan.FromSeconds(durationSeconds);
            if (State == CircuitState.Closed)
                return true;

            if (State == CircuitState.Open)
            {
                if (_time.GetElapsedTime(_openedAtTimestamp) >= duration)
                {
                    State = CircuitState.HalfOpen;
                    _halfOpenProbeInFlight = true;
                    _probeStartedTimestamp = _time.GetTimestamp();
                    return true;
                }
                return false;
            }

            // HalfOpen: exactly one probe, but a probe that never reported back must not wedge the breaker forever.
            if (!_halfOpenProbeInFlight || _time.GetElapsedTime(_probeStartedTimestamp) >= duration)
            {
                _halfOpenProbeInFlight = true;
                _probeStartedTimestamp = _time.GetTimestamp();
                return true;
            }
            return false;
        }
    }

    public void OnSuccess()
    {
        lock (_lock)
        {
            _consecutiveFailures = 0;
            _halfOpenProbeInFlight = false;
            State = CircuitState.Closed;
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
                _openedAtTimestamp = _time.GetTimestamp();
            }
        }
    }
}

public static class CircuitBreakerRegistry
{
    private static readonly ConcurrentDictionary<string, CircuitBreakerState> _breakers = new();

    public static CircuitBreakerState GetOrCreate(string key) =>
        _breakers.GetOrAdd(key, _ => new CircuitBreakerState());

    public static CircuitBreakerState GetOrCreate(string key, TimeProvider timeProvider) =>
        _breakers.GetOrAdd(key, _ => new CircuitBreakerState(timeProvider));
}

public static class ResilienceServiceCollectionExtensions
{
    public static IServiceCollection AddLinKitResilience(this IServiceCollection services)
    {
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton(services, TimeProvider.System);
        return services;
    }
}
