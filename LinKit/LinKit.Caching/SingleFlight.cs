using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LinKit.Caching;

/// <summary>
/// Keyed single-flight coordinator shared by the cache providers: concurrent misses for the same key join one
/// factory invocation.
/// <para>
/// Two properties matter for correctness in a server:
/// <list type="bullet">
/// <item>A flight is cancelled as soon as its <em>last</em> waiter gives up, so a cancelled request does not
/// keep an abandoned factory running (the factory receives the flight token, not the first caller's token, so
/// one cancelled caller never fails the remaining ones).</item>
/// <item>A flight that outlives the configured maximum lifetime is evicted, so a factory that ignores
/// cancellation cannot pin a key - and its dictionary entry - forever.</item>
/// </list>
/// </para>
/// </summary>
internal sealed class SingleFlight<TKey>
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, Flight> _flights = new();
    private readonly TimeSpan _maxLifetime;

    public SingleFlight(TimeSpan maxLifetime) => _maxLifetime = maxLifetime;

    public int Count => _flights.Count;

    /// <summary>Marks a running flight for <paramref name="key"/> as stale; its result will not be published.</summary>
    public void Invalidate(TKey key)
    {
        if (_flights.TryGetValue(key, out Flight? flight))
        {
            flight.Invalidate();
        }
    }

    /// <summary>
    /// Returns the value produced by <paramref name="factory"/>, invoking it at most once per key while a flight
    /// is in progress. <paramref name="publish"/> runs only on the owner of the flight and decides whether the
    /// result may be written to the cache layer.
    /// </summary>
    public async ValueTask<T> RunAsync<T>(
        TKey key,
        Func<CancellationToken, Task<T>> factory,
        Func<Flight, T, ValueTask> publish,
        CancellationToken cancellationToken
    )
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Flight candidate = new();
            Flight flight = _flights.GetOrAdd(key, candidate);
            if (ReferenceEquals(flight, candidate))
            {
                // Fire-and-forget: the owner detaches so every waiter, including this one, awaits the same task.
                _ = ExecuteAsync(key, factory, publish, flight);
            }
            else if (attempt < 2 && flight.ExceedsLifetime(_maxLifetime) && TryRemove(key, flight))
            {
                // Abandoned flight: fail it so its (possible) awaiters stop, then retry with a fresh one.
                flight.Cancel();
                flight.Fail(
                    new TimeoutException(
                        $"Single-flight factory for '{key}' exceeded the {_maxLifetime.TotalSeconds:0.###}s lifetime."
                    )
                );
                continue;
            }

            flight.AddWaiter();
            try
            {
                var result = await flight
                    .Completion.WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                flight.RemoveWaiter();
                return (T)result!;
            }
            catch
            {
                if (flight.RemoveWaiter() == 0)
                {
                    // Nobody is waiting any more: stop paying for a result that will be thrown away.
                    flight.Cancel();
                }
                throw;
            }
        }
    }

    private async Task ExecuteAsync<T>(
        TKey key,
        Func<CancellationToken, Task<T>> factory,
        Func<Flight, T, ValueTask> publish,
        Flight flight
    )
    {
        object? value = null;
        Exception? error = null;
        try
        {
            T created = await factory(flight.Token).ConfigureAwait(false);
            await publish(flight, created).ConfigureAwait(false);
            value = created;
        }
        catch (Exception ex)
        {
            error = ex;
        }

        // Removed before completion is published so a finished (even faulted) flight is never observable.
        TryRemove(key, flight);

        if (error is null)
        {
            flight.Complete(value);
        }
        else
        {
            flight.Fail(error);
        }
    }

    private bool TryRemove(TKey key, Flight flight) =>
        ((ICollection<KeyValuePair<TKey, Flight>>)_flights).Remove(
            new KeyValuePair<TKey, Flight>(key, flight)
        );

    internal sealed class Flight
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly TaskCompletionSource<object?> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly object _gate = new();
        private readonly long _startedTicks = Environment.TickCount64;
        private bool _invalidated;
        private int _waiters;

        /// <summary>Cancelled when the flight is abandoned (last waiter gone) or evicted for exceeding its lifetime.</summary>
        public CancellationToken Token => _cancellation.Token;

        public Task<object?> Completion => _completion.Task;

        /// <summary>Set by Set/Remove on the same key while this flight is running.</summary>
        public bool Invalidated
        {
            get
            {
                lock (_gate)
                    return _invalidated;
            }
        }

        public void Invalidate()
        {
            lock (_gate)
                _invalidated = true;
        }

        public void Cancel()
        {
            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException) { }
            catch (AggregateException) { }
        }

        public int AddWaiter() => Interlocked.Increment(ref _waiters);

        public int RemoveWaiter() => Interlocked.Decrement(ref _waiters);

        public bool ExceedsLifetime(TimeSpan maxLifetime) =>
            maxLifetime > TimeSpan.Zero
            && Environment.TickCount64 - _startedTicks > (long)maxLifetime.TotalMilliseconds;

        public void Complete(object? value) => _completion.TrySetResult(value);

        public void Fail(Exception error)
        {
            _completion.TrySetException(error);
            // Mark observed so an unwatched flight never raises UnobservedTaskException.
            _ = _completion.Task.Exception;
        }
    }
}
