using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;
using LinKit.Core.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LinKit.Caching;

/// <summary>
/// Two-level (L1 In-Memory + L2 Redis) cache provider that is 100% Native AOT compatible.
/// Uses <see cref="LinKitMessageJson"/> source-generated type info for L2 Redis serialization.
/// Lookups are found-based: cached 0, false, Guid.Empty and null are hits, not misses.
/// L2 values are wrapped in a small binary envelope carrying the absolute expiry so an L1
/// backfill never outlives the Redis TTL (no extra round-trip per get).
/// </summary>
public sealed class HybridRedisCacheProvider : ICacheProvider, IDisposable
{
    // Envelope: [0x01 marker][8 bytes big-endian absolute expiry, Unix ms][JSON payload].
    // The marker byte cannot start a JSON/UTF-8 payload written by LinKitMessageJson, so legacy
    // (pre-envelope) values remain readable; they are returned but never backfilled into L1
    // because their remaining TTL is unknown.
    private const byte EnvelopeMarker = 0x01;
    private const int EnvelopeHeaderSize = 1 + 8;
    private const long NoExpiry = long.MaxValue;

    /// <summary>A factory that never completes must not pin its key in the flight table forever.</summary>
    private static readonly TimeSpan DefaultFlightMaxLifetime = TimeSpan.FromSeconds(30);

    private readonly InMemoryCacheProvider _l1;
    private readonly Resp2RedisClient? _redis;
    private readonly LinKitRedisOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SingleFlight<string> _flight = new(DefaultFlightMaxLifetime);

    public HybridRedisCacheProvider(
        LinKitRedisOptions? options = null,
        TimeProvider? timeProvider = null,
        ILoggerFactory? loggerFactory = null
    )
    {
        _options = options ?? new LinKitRedisOptions();
        _time = timeProvider ?? TimeProvider.System;
        _logger =
            loggerFactory?.CreateLogger<HybridRedisCacheProvider>()
            ?? (ILogger)NullLogger<HybridRedisCacheProvider>.Instance;
        _l1 = new InMemoryCacheProvider(10_000, _time);
        if (_options.Enabled)
        {
            _redis = new Resp2RedisClient(
                _options,
                loggerFactory?.CreateLogger<Resp2RedisClient>()
            );
        }
    }

    private long NowUnixMs => _time.GetUtcNow().ToUnixTimeMilliseconds();

    private void InvalidateFlight(string key) => _flight.Invalidate(key);

    public async ValueTask<T?> GetAsync<T>(
        string key,
        CancellationToken cancellationToken = default
    )
    {
        (bool found, T? value) = await TryGetFoundAsync<T>(key, cancellationToken)
            .ConfigureAwait(false);
        return found ? value : default;
    }

    /// <summary>Hit/miss lookup that does not depend on default(T): 0, false, Guid.Empty and cached null are hits.</summary>
    private async ValueTask<(bool Found, T? Value)> TryGetFoundAsync<T>(
        string key,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 1. Check L1 In-Memory (found-based, mirrors InMemoryCacheProvider semantics).
        if (_l1.TryGetFound<T>(key, out T? l1Value))
        {
            return (true, l1Value);
        }

        // 2. Check L2 Redis. A nil reply (null bytes) is a miss; any non-null payload —
        // including a serialized 0/false/Guid.Empty — is a hit.
        if (_redis is null)
        {
            return (false, default);
        }

        try
        {
            byte[]? bytes = await _redis
                .GetBytesAsync(key, cancellationToken)
                .ConfigureAwait(false);
            if (bytes is null)
            {
                return (false, default);
            }

            long nowMs = NowUnixMs;
            int payloadOffset = 0;
            TimeSpan? backfillTtl = null;
            bool canBackfill = false;

            if (bytes.Length >= EnvelopeHeaderSize && bytes[0] == EnvelopeMarker)
            {
                long expiresAtMs = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(1, 8));
                if (expiresAtMs != NoExpiry)
                {
                    long remainingMs = expiresAtMs - nowMs;
                    if (remainingMs <= 0)
                    {
                        return (false, default); // expired in L2; never resurrect into L1
                    }
                    backfillTtl = TimeSpan.FromMilliseconds(remainingMs);
                }
                payloadOffset = EnvelopeHeaderSize;
                canBackfill = true;
            }

            if (bytes.Length == payloadOffset)
            {
                return (false, default);
            }

            T? value = DeserializeFromBytes<T>(bytes.AsSpan(payloadOffset));
            if (canBackfill)
            {
                // L1 must never outlive L2: backfill with the REMAINING envelope TTL, not DefaultTtl.
                await _l1.SetAsync(key, value, backfillTtl, null, cancellationToken)
                    .ConfigureAwait(false);
            }
            return (true, value);
        }
        catch (RedisServerException)
        {
            // Server-side misconfiguration (e.g. WRONGTYPE) must be visible, not a silent perpetual miss.
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Graceful fallback to L1 on serialization errors or transient Redis problems.
            _logger.LogWarning(
                ex,
                "Redis L2 read for key '{Key}' failed; falling back to a cache miss.",
                key
            );
            return (false, default);
        }
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
        InvalidateFlight(key);
        await SetCoreAsync(key, value, ttl, sliding, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask SetCoreAsync<T>(
        string key,
        T value,
        TimeSpan? ttl,
        TimeSpan? sliding,
        CancellationToken cancellationToken
    )
    {
        TimeSpan effectiveTtl = ttl ?? _options.DefaultTtl;
        await _l1.SetAsync(key, value, effectiveTtl, sliding, cancellationToken)
            .ConfigureAwait(false);

        if (_redis is null)
        {
            return;
        }

        await TrySetRedisAsync(key, value, effectiveTtl, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask TrySetRedisAsync<T>(
        string key,
        T value,
        TimeSpan effectiveTtl,
        CancellationToken cancellationToken
    )
    {
        try
        {
            byte[] payload = SerializeToBytes(value);
            long expiresAtMs =
                effectiveTtl > TimeSpan.Zero
                    ? NowUnixMs + (long)effectiveTtl.TotalMilliseconds
                    : NoExpiry;
            byte[] envelope = new byte[EnvelopeHeaderSize + payload.Length];
            envelope[0] = EnvelopeMarker;
            BinaryPrimitives.WriteInt64BigEndian(envelope.AsSpan(1, 8), expiresAtMs);
            payload.CopyTo(envelope, EnvelopeHeaderSize);
            await _redis!
                .SetBytesAsync(key, envelope, effectiveTtl, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RedisServerException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Ignore Redis L2 write errors if the type is not registered or Redis is temporarily unreachable.
            _logger.LogWarning(
                ex,
                "Redis L2 write for key '{Key}' failed; value is cached in L1 only.",
                key
            );
        }
    }

    public async ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        InvalidateFlight(key);
        await _l1.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
        if (_redis is not null)
        {
            await _redis.DeleteKeyAsync(key, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Invalidates a cache namespace: drops every L1 entry and every L2 entry whose key starts with
    /// <paramref name="keyPrefix"/>. L2 uses SCAN + batched DEL, so Redis is never blocked by a KEYS command.
    /// Values written by another process between SCAN batches may survive; this is best-effort by design.
    /// </summary>
    public async ValueTask<int> RemoveByPrefixAsync(
        string keyPrefix,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(keyPrefix);
        var removed = await _l1.RemoveByPrefixAsync(keyPrefix, cancellationToken)
            .ConfigureAwait(false);
        if (_redis is not null)
        {
            removed += await _redis
                .DeleteByPrefixAsync(keyPrefix, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        return removed;
    }

    public ValueTask<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        // The fast path stays synchronous; the flight dovetails the L2 re-check with the factory so a value
        // another process just wrote is returned without a second cache round-trip.
        return GetOrCreateCoreAsync(key, factory, ttl, cancellationToken);

        async ValueTask<T> GetOrCreateCoreAsync(
            string cacheKey,
            Func<CancellationToken, Task<T>> create,
            TimeSpan? requestedTtl,
            CancellationToken token
        )
        {
            (bool found, T? existing) = await TryGetFoundAsync<T>(cacheKey, token)
                .ConfigureAwait(false);
            if (found)
            {
                return existing!;
            }

            var fromL2 = false;
            return await _flight
                .RunAsync<T>(
                    cacheKey,
                    async flightToken =>
                    {
                        // Re-check L2 under the flight: another process may have filled the key meanwhile.
                        (bool recheckedFound, T? rechecked) = await TryGetFoundAsync<T>(
                                cacheKey,
                                flightToken
                            )
                            .ConfigureAwait(false);
                        if (recheckedFound)
                        {
                            fromL2 = true; // already in L1 with its remaining L2 TTL, do not overwrite it below
                            return rechecked!;
                        }
                        return await create(flightToken).ConfigureAwait(false);
                    },
                    (flight, created) => PublishAsync(flight, created),
                    token
                )
                .ConfigureAwait(false);

            async ValueTask PublishAsync(SingleFlight<string>.Flight current, T created)
            {
                if (requestedTtl.HasValue && requestedTtl.Value <= TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(nameof(ttl), "TTL must be positive.");
                }
                if (fromL2 || current.Invalidated)
                {
                    return;
                }

                TimeSpan effectiveTtl = requestedTtl ?? _options.DefaultTtl;

                // No blocking wait under a lock: the invalidation check is a snapshot, the write is awaited normally.
                await _l1.SetAsync(cacheKey, created, effectiveTtl, null, CancellationToken.None)
                    .ConfigureAwait(false);

                // Best effort for L2: without a distributed lock a concurrent Set racing this write cannot be excluded.
                if (!current.Invalidated && _redis is not null)
                {
                    await TrySetRedisAsync(cacheKey, created, effectiveTtl, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
        }
    }

    private static byte[] SerializeToBytes<T>(T value)
    {
        if (value is null)
        {
            return "null"u8.ToArray();
        }
        return LinKitMessageJson.SerializeToUtf8Bytes(value);
    }

    private static T? DeserializeFromBytes<T>(ReadOnlySpan<byte> bytes)
    {
        if (bytes.SequenceEqual("null"u8))
        {
            return default;
        }
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
