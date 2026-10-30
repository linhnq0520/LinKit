using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LinKit.Core.Messaging;

namespace LinKit.Caching;

/// <summary>
/// Two-level (L1 In-Memory + L2 Redis) cache provider that is 100% Native AOT compatible.
/// Uses <see cref="LinKitMessageJson"/> source-generated type info for L2 Redis serialization.
/// </summary>
public sealed class HybridRedisCacheProvider : ICacheProvider, IDisposable
{
    private readonly InMemoryCacheProvider _l1;
    private readonly Resp2RedisClient? _redis;
    private readonly LinKitRedisOptions _options;
    private readonly ConcurrentDictionary<string, Flight> _inflight = new();

    private sealed class Flight
    {
        public TaskCompletionSource<object?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public HybridRedisCacheProvider(LinKitRedisOptions? options = null)
    {
        _options = options ?? new LinKitRedisOptions();
        _l1 = new InMemoryCacheProvider();
        if (_options.Enabled)
        {
            _redis = new Resp2RedisClient(_options);
        }
    }

    public async ValueTask<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 1. Check L1 In-Memory
        T? l1Val = await _l1.GetAsync<T>(key, cancellationToken).ConfigureAwait(false);
        if (l1Val is not null && !EqualityComparer<T>.Default.Equals(l1Val, default!))
        {
            return l1Val;
        }

        // 2. Check L2 Redis
        if (_redis is null)
        {
            return default;
        }

        try
        {
            byte[]? bytes = await _redis.GetBytesAsync(key, cancellationToken).ConfigureAwait(false);
            if (bytes is { Length: > 0 })
            {
                T? value = DeserializeFromBytes<T>(bytes);
                if (value is not null && !EqualityComparer<T>.Default.Equals(value, default!))
                {
                    await _l1.SetAsync(key, value, _options.DefaultTtl, null, cancellationToken)
                        .ConfigureAwait(false);
                    return value;
                }
            }
        }
        catch
        {
            // Graceful fallback to L1 on Redis or serialization errors
        }

        return default;
    }

    public async ValueTask SetAsync<T>(
        string key,
        T value,
        TimeSpan? ttl = null,
        TimeSpan? sliding = null,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        TimeSpan effectiveTtl = ttl ?? _options.DefaultTtl;
        await _l1.SetAsync(key, value, effectiveTtl, sliding, cancellationToken)
            .ConfigureAwait(false);

        if (_redis is null || value is null)
        {
            return;
        }

        try
        {
            byte[] bytes = SerializeToBytes(value);
            await _redis.SetBytesAsync(key, bytes, effectiveTtl, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // Ignore Redis L2 write errors if type is not registered or Redis is temporarily unreachable
        }
    }

    public async ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        await _l1.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
        if (_redis is not null)
        {
            await _redis.DeleteKeyAsync(key, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        T? existing = await GetAsync<T>(key, cancellationToken).ConfigureAwait(false);
        if (existing is not null && !EqualityComparer<T>.Default.Equals(existing, default!))
        {
            return existing;
        }

        Flight candidate = new();
        Flight flight = _inflight.GetOrAdd(key, candidate);
        if (ReferenceEquals(flight, candidate))
        {
            _ = RunFlightAsync(key, factory, ttl, flight);
        }

        return (T)(await flight.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private async Task RunFlightAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan? ttl,
        Flight flight
    )
    {
        object? value = null;
        Exception? error = null;
        try
        {
            T? existing = await GetAsync<T>(key, CancellationToken.None).ConfigureAwait(false);
            if (existing is not null && !EqualityComparer<T>.Default.Equals(existing, default!))
            {
                value = existing;
            }
            else
            {
                T created = await factory(CancellationToken.None).ConfigureAwait(false);
                if (created is not null && !EqualityComparer<T>.Default.Equals(created, default!))
                {
                    await SetAsync(key, created, ttl, null, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                value = created;
            }
        }
        catch (Exception ex)
        {
            error = ex;
        }

        ((ICollection<KeyValuePair<string, Flight>>)_inflight).Remove(
            new KeyValuePair<string, Flight>(key, flight)
        );

        if (error is null)
        {
            flight.Completion.TrySetResult(value);
        }
        else
        {
            flight.Completion.TrySetException(error);
            _ = flight.Completion.Task.Exception;
        }
    }

    private static byte[] SerializeToBytes<T>(T value)
    {
        return LinKitMessageJson.SerializeToUtf8Bytes(value!);
    }

    private static T? DeserializeFromBytes<T>(byte[] bytes)
    {
        if (typeof(T) == typeof(string) && bytes.Length > 0 && bytes[0] != (byte)'"')
        {
            return (T)(object)System.Text.Encoding.UTF8.GetString(bytes);
        }
        return LinKitMessageJson.Deserialize<T>(bytes);
    }

    public void Dispose()
    {
        _redis?.Dispose();
    }
}

