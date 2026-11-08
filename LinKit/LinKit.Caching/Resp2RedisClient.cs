using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LinKit.Caching;

/// <summary>
/// Options for <see cref="HybridRedisCacheProvider"/>.
/// </summary>
public sealed class LinKitRedisOptions
{
    public const string DefaultSection = "Redis";

    public bool Enabled { get; set; } = true;
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 6379;

    /// <summary>
    /// Redis 6+ ACL user name. When set, the client sends <c>AUTH &lt;username&gt; &lt;password&gt;</c> instead of
    /// the legacy single-password <c>AUTH &lt;password&gt;</c>.
    /// </summary>
    public string? Username { get; set; }

    public string? Password { get; set; }
    public int Database { get; set; } = 0;

    /// <summary>Prefix prepended to every key so several applications can share one Redis database.</summary>
    public string? KeyPrefix { get; set; }

    /// <summary>Connects through TLS (managed Redis providers usually require this).</summary>
    public bool UseTls { get; set; }

    /// <summary>
    /// SNI/certificate host. Managed Redis endpoints often expose an internal host name, so the TLS host can
    /// differ from <see cref="Host"/>; defaults to <see cref="Host"/> when not set.
    /// </summary>
    public string? TlsHost { get; set; }

    public TimeSpan DefaultTtl { get; set; } = TimeSpan.FromMinutes(60);
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Maximum time a single command (write + read) may take before the connection is dropped
    /// and the operation is treated as a cache miss. Prevents a hung Redis from stalling callers.
    /// </summary>
    public TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Collects configuration problems; the list is empty when the options are usable. Registration calls this so a
    /// typo in an address or timeout fails at startup instead of turning every cache read into a miss.
    /// </summary>
    internal IReadOnlyList<string> Validate()
    {
        List<string> errors = [];

        if (string.IsNullOrWhiteSpace(Host))
            errors.Add("Host must not be empty.");
        if (Port is < 1 or > 65535)
            errors.Add("Port must be between 1 and 65535.");
        if (Database < 0)
            errors.Add("Database must not be negative.");
        if (DefaultTtl <= TimeSpan.Zero)
            errors.Add("DefaultTtl must be positive.");
        if (ConnectTimeout <= TimeSpan.Zero)
            errors.Add("ConnectTimeout must be positive.");
        if (ReadTimeout <= TimeSpan.Zero)
            errors.Add("ReadTimeout must be positive.");

        return errors;
    }
}

/// <summary>
/// Thrown when the Redis server replies with a protocol error (-ERR ...), e.g. a wrong password,
/// an invalid SELECT database or a wrong-type operation. Never swallowed as a cache miss.
/// </summary>
public sealed class RedisServerException : Exception
{
    public RedisServerException(string message)
        : base(message) { }
}

/// <summary>
/// Lightweight, zero-reflection, 100% Native AOT compatible RESP2 Redis client with connection pooling.
/// Error policy: transient network failures and read timeouts are logged and surface as a miss
/// (null/false); server -ERR replies throw <see cref="RedisServerException"/> so misconfiguration
/// (bad AUTH/SELECT) fails visibly instead of silently returning misses on a poisoned connection.
/// </summary>
public sealed class Resp2RedisClient : IDisposable
{
    private readonly LinKitRedisOptions _options;
    private readonly ILogger _logger;
    private readonly ConcurrentBag<PooledConnection> _pool = new();
    private readonly string _keyPrefix;
    private const int MaxPoolSize = 32;

    /// <summary>Hard stop for SCAN loops so a misbehaving server cannot spin forever.</summary>
    private const int MaxScanIterations = 10_000;

    public Resp2RedisClient(LinKitRedisOptions options, ILogger<Resp2RedisClient>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Host))
            throw new ArgumentException("Redis host must not be empty.", nameof(options));
        if (options.Port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.Port,
                "Redis port must be between 1 and 65535."
            );
        if (options.ConnectTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "ConnectTimeout must be positive."
            );
        if (options.ReadTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "ReadTimeout must be positive.");
        if (options.Database < 0)
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Database must not be negative."
            );
        if (!string.IsNullOrEmpty(options.Username) && string.IsNullOrEmpty(options.Password))
            throw new ArgumentException(
                "A Redis ACL username requires a password.",
                nameof(options)
            );
        if (options.UseTls && string.IsNullOrWhiteSpace(options.TlsHost ?? options.Host))
            throw new ArgumentException(
                "TLS requires a host name for certificate validation.",
                nameof(options)
            );

        _options = options;
        _keyPrefix = options.KeyPrefix ?? string.Empty;
        _logger = logger ?? (ILogger)NullLogger<Resp2RedisClient>.Instance;
    }

    public async Task<byte[]?> GetBytesAsync(string key, CancellationToken ct = default)
    {
        Reply? reply = await ExecuteAsync(
                "GET",
                key,
                buffer =>
                {
                    AppendHeader(buffer, 2);
                    AppendBulkString(buffer, "GET");
                    AppendBulkString(buffer, Prefixed(key));
                },
                ct
            )
            .ConfigureAwait(false);

        return reply is { Kind: ReplyKind.Bulk } ? reply.Value : null;
    }

    public async Task<bool> SetBytesAsync(
        string key,
        byte[] value,
        TimeSpan? ttl,
        CancellationToken ct = default
    )
    {
        TimeSpan effectiveTtl = ttl ?? _options.DefaultTtl;
        long milliseconds = effectiveTtl > TimeSpan.Zero ? (long)effectiveTtl.TotalMilliseconds : 0;

        Reply? reply = await ExecuteAsync(
                "SET",
                key,
                buffer => AppendSetCommand(buffer, Prefixed(key), value, milliseconds),
                ct
            )
            .ConfigureAwait(false);

        return reply is { Kind: ReplyKind.SimpleString } && IsOk(reply.Text);
    }

    public async Task<bool> DeleteKeyAsync(string key, CancellationToken ct = default)
    {
        Reply? reply = await ExecuteAsync(
                "DEL",
                key,
                buffer =>
                {
                    AppendHeader(buffer, 2);
                    AppendBulkString(buffer, "DEL");
                    AppendBulkString(buffer, Prefixed(key));
                },
                ct
            )
            .ConfigureAwait(false);

        return reply is { Kind: ReplyKind.Integer } && reply.Integer > 0;
    }

    /// <summary>True when the key exists (EXISTS is O(1) and never returns the value).</summary>
    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        Reply? reply = await ExecuteAsync(
                "EXISTS",
                key,
                buffer =>
                {
                    AppendHeader(buffer, 2);
                    AppendBulkString(buffer, "EXISTS");
                    AppendBulkString(buffer, Prefixed(key));
                },
                ct
            )
            .ConfigureAwait(false);

        return reply is { Kind: ReplyKind.Integer } && reply.Integer > 0;
    }

    /// <summary>Remaining time to live, or <c>null</c> when the key is missing or has no expiry.</summary>
    public async Task<TimeSpan?> TtlAsync(string key, CancellationToken ct = default)
    {
        Reply? reply = await ExecuteAsync(
                "TTL",
                key,
                buffer =>
                {
                    AppendHeader(buffer, 2);
                    AppendBulkString(buffer, "TTL");
                    AppendBulkString(buffer, Prefixed(key));
                },
                ct
            )
            .ConfigureAwait(false);

        if (reply is not { Kind: ReplyKind.Integer } || reply.Integer < 0)
        {
            return null;
        }
        return TimeSpan.FromSeconds(reply.Integer);
    }

    /// <summary>Applies (or replaces) an expiry; false when the key does not exist.</summary>
    public async Task<bool> ExpireAsync(string key, TimeSpan ttl, CancellationToken ct = default)
    {
        if (ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl), "TTL must be positive.");

        long milliseconds = (long)ttl.TotalMilliseconds;
        Reply? reply = await ExecuteAsync(
                "PEXPIRE",
                key,
                buffer =>
                {
                    AppendHeader(buffer, 3);
                    AppendBulkString(buffer, "PEXPIRE");
                    AppendBulkString(buffer, Prefixed(key));
                    AppendBulkString(buffer, milliseconds.ToString(CultureInfo.InvariantCulture));
                },
                ct
            )
            .ConfigureAwait(false);

        return reply is { Kind: ReplyKind.Integer } && reply.Integer == 1;
    }

    /// <summary>
    /// Atomically increments a counter. Returns <c>null</c> when the command could not be executed, so a counter
    /// is never silently reset to zero by a transient network failure. When <paramref name="ttl"/> is supplied and
    /// this call created the counter, the expiry window is attached to it.
    /// </summary>
    public async Task<long?> IncrementAsync(
        string key,
        long delta = 1,
        TimeSpan? ttl = null,
        CancellationToken ct = default
    )
    {
        Reply? reply = await ExecuteAsync(
                "INCRBY",
                key,
                buffer =>
                {
                    AppendHeader(buffer, 3);
                    AppendBulkString(buffer, "INCRBY");
                    AppendBulkString(buffer, Prefixed(key));
                    AppendBulkString(buffer, delta.ToString(CultureInfo.InvariantCulture));
                },
                ct
            )
            .ConfigureAwait(false);

        if (reply is not { Kind: ReplyKind.Integer })
        {
            return null;
        }

        if (ttl.HasValue && ttl.Value > TimeSpan.Zero && reply.Integer == delta)
        {
            await ExpireAsync(key, ttl.Value, ct).ConfigureAwait(false);
        }
        return reply.Integer;
    }

    /// <summary>MGET: one round-trip for many keys. Missing entries are <c>null</c>.</summary>
    public async Task<IReadOnlyList<byte[]?>> GetManyAsync(
        IReadOnlyList<string> keys,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(keys);
        var result = new byte[]?[keys.Count];
        if (keys.Count == 0)
        {
            return result;
        }

        Reply? reply = await ExecuteAsync(
                "MGET",
                keys[0],
                buffer =>
                {
                    AppendHeader(buffer, keys.Count + 1);
                    AppendBulkString(buffer, "MGET");
                    for (int i = 0; i < keys.Count; i++)
                    {
                        AppendBulkString(buffer, Prefixed(keys[i]));
                    }
                },
                ct
            )
            .ConfigureAwait(false);

        if (reply?.Items is not { } items)
        {
            return result;
        }

        for (int i = 0; i < result.Length && i < items.Length; i++)
        {
            result[i] = items[i] is { Kind: ReplyKind.Bulk, Value: { } value } ? value : null;
        }
        return result;
    }

    /// <summary>MSET equivalent: all commands are pipelined, so N writes cost one round-trip.</summary>
    public async Task<int> SetManyAsync(
        IReadOnlyList<KeyValuePair<string, byte[]>> items,
        TimeSpan? ttl = null,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return 0;
        }

        TimeSpan effectiveTtl = ttl ?? _options.DefaultTtl;
        long milliseconds = effectiveTtl > TimeSpan.Zero ? (long)effectiveTtl.TotalMilliseconds : 0;

        PooledConnection? conn = await RentAsync(ct).ConfigureAwait(false);
        if (conn is null)
        {
            return 0;
        }

        int capacity = Math.Max(256, Math.Min(256 * 1024, items.Count * 128));
        var buffer = new ArrayBufferWriter<byte>(capacity);
        for (int i = 0; i < items.Count; i++)
        {
            AppendSetCommand(buffer, Prefixed(items[i].Key), items[i].Value, milliseconds);
        }

        bool failed = false;
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.ReadTimeout);
            await WriteAsync(conn, buffer, cts.Token).ConfigureAwait(false);

            int stored = 0;
            for (int i = 0; i < items.Count; i++)
            {
                Reply reply = await ReadReplyAsync(conn, cts.Token).ConfigureAwait(false);
                if (reply is { Kind: ReplyKind.SimpleString } && IsOk(reply.Text))
                {
                    stored++;
                }
            }
            return stored;
        }
        catch (Exception ex) when (IsTransient(ex, ct))
        {
            failed = true;
            _logger.LogWarning(ex, "Redis MSET of {Count} keys failed.", items.Count);
            return 0;
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            if (failed)
                conn.Dispose();
            else
                Return(conn);
        }
    }

    /// <summary>
    /// Deletes every key matching <paramref name="keyPrefix"/> using SCAN + batched DEL, so a large namespace is
    /// removed without ever blocking Redis with KEYS. Returns the number of keys deleted.
    /// </summary>
    public async Task<int> DeleteByPrefixAsync(
        string keyPrefix,
        int scanBatchSize = 500,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(keyPrefix);
        if (scanBatchSize < 1)
            throw new ArgumentOutOfRangeException(
                nameof(scanBatchSize),
                "Scan batch size must be positive."
            );

        string match = Prefixed(keyPrefix) + "*";
        string count = scanBatchSize.ToString(CultureInfo.InvariantCulture);
        var removed = 0;
        var cursor = "0";
        var iterations = 0;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            string current = cursor;

            Reply? page = await ExecuteAsync(
                    "SCAN",
                    keyPrefix,
                    buffer =>
                    {
                        AppendHeader(buffer, 6);
                        AppendBulkString(buffer, "SCAN");
                        AppendBulkString(buffer, current);
                        AppendBulkString(buffer, "MATCH");
                        AppendBulkString(buffer, match);
                        AppendBulkString(buffer, "COUNT");
                        AppendBulkString(buffer, count);
                    },
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (page?.Items is not { } parts || parts.Length != 2)
            {
                break;
            }
            if (parts[0].Kind != ReplyKind.Bulk || parts[0].Value is not { } cursorBytes)
            {
                break;
            }

            cursor = Encoding.UTF8.GetString(cursorBytes);

            if (parts[1].Items is { Length: > 0 } batch)
            {
                var keys = new List<string>(batch.Length);
                foreach (Reply entry in batch)
                {
                    if (entry is { Kind: ReplyKind.Bulk, Value: { } raw })
                    {
                        keys.Add(Encoding.UTF8.GetString(raw));
                    }
                }
                removed += await DeleteRawAsync(keys, cancellationToken).ConfigureAwait(false);
            }
        } while (cursor != "0" && ++iterations < MaxScanIterations);

        return removed;
    }

    // Keys here are already prefixed: they came back from SCAN in their final wire form.
    private async Task<int> DeleteRawAsync(IReadOnlyList<string> redisKeys, CancellationToken ct)
    {
        if (redisKeys.Count == 0)
        {
            return 0;
        }

        Reply? reply = await ExecuteAsync(
                "DEL",
                null,
                buffer =>
                {
                    AppendHeader(buffer, redisKeys.Count + 1);
                    AppendBulkString(buffer, "DEL");
                    for (int i = 0; i < redisKeys.Count; i++)
                    {
                        AppendBulkString(buffer, redisKeys[i]);
                    }
                },
                ct
            )
            .ConfigureAwait(false);

        return reply is { Kind: ReplyKind.Integer } ? (int)reply.Integer : 0;
    }

    private string Prefixed(string key) => _keyPrefix + key;

    /// <summary>
    /// Rents a connection, writes one command and reads one reply. Transient failures are logged and surface as
    /// <c>null</c>; server errors and malformed protocol replies propagate.
    /// </summary>
    private async Task<Reply?> ExecuteAsync(
        string operation,
        string? key,
        Action<ArrayBufferWriter<byte>> build,
        CancellationToken ct
    )
    {
        PooledConnection? conn = await RentAsync(ct).ConfigureAwait(false);
        if (conn is null)
        {
            return null;
        }

        var buffer = new ArrayBufferWriter<byte>(256);
        build(buffer);

        bool failed = false;
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.ReadTimeout);
            await WriteAsync(conn, buffer, cts.Token).ConfigureAwait(false);
            return await ReadReplyAsync(conn, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransient(ex, ct))
        {
            failed = true;
            _logger.LogWarning(
                ex,
                "Redis {Operation} for key '{Key}' failed.",
                operation,
                key ?? "-"
            );
            return null;
        }
        catch
        {
            // Server -ERR, malformed protocol or caller cancellation: connection state is unknown.
            failed = true;
            throw;
        }
        finally
        {
            if (failed)
                conn.Dispose();
            else
                Return(conn);
        }
    }

    // Transient = network I/O problems or our own ReadTimeout firing (caller token not cancelled).
    // Caller cancellation and server/protocol errors are never hidden as misses.
    private static bool IsTransient(Exception ex, CancellationToken callerToken) =>
        ex is IOException or SocketException or EndOfStreamException
        || (ex is OperationCanceledException && !callerToken.IsCancellationRequested);

    private static bool IsOk(string text) =>
        string.Equals(text, "OK", StringComparison.OrdinalIgnoreCase);

    private async Task<PooledConnection?> RentAsync(CancellationToken ct)
    {
        while (_pool.TryTake(out PooledConnection? existing))
        {
            if (existing.IsConnected)
            {
                return existing;
            }
            existing.Dispose();
        }

        PooledConnection? conn = null;
        TcpClient? tcp = null;
        try
        {
            tcp = new TcpClient { NoDelay = true };
            using (
                CancellationTokenSource connectCts =
                    CancellationTokenSource.CreateLinkedTokenSource(ct)
            )
            {
                connectCts.CancelAfter(_options.ConnectTimeout);
                await tcp.ConnectAsync(_options.Host, _options.Port, connectCts.Token)
                    .ConfigureAwait(false);
            }

            Stream transport = await OpenTransportAsync(tcp, ct).ConfigureAwait(false);
            conn = new PooledConnection(tcp, transport);

            using CancellationTokenSource handshakeCts =
                CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshakeCts.CancelAfter(_options.ReadTimeout);

            if (!string.IsNullOrEmpty(_options.Password))
            {
                var buffer = new ArrayBufferWriter<byte>(96);
                string? username = string.IsNullOrEmpty(_options.Username)
                    ? null
                    : _options.Username;
                AppendHeader(buffer, username is null ? 2 : 3);
                AppendBulkString(buffer, "AUTH");
                if (username is not null)
                {
                    AppendBulkString(buffer, username);
                }
                AppendBulkString(buffer, _options.Password!);
                await WriteAsync(conn, buffer, handshakeCts.Token).ConfigureAwait(false);

                Reply authReply = await ReadReplyAsync(conn, handshakeCts.Token)
                    .ConfigureAwait(false);
                if (authReply is not { Kind: ReplyKind.SimpleString } || !IsOk(authReply.Text))
                {
                    throw new RedisServerException($"Unexpected AUTH reply: {authReply.Text}");
                }
            }

            if (_options.Database > 0)
            {
                var buffer = new ArrayBufferWriter<byte>(64);
                AppendHeader(buffer, 2);
                AppendBulkString(buffer, "SELECT");
                AppendBulkString(buffer, _options.Database.ToString(CultureInfo.InvariantCulture));
                await WriteAsync(conn, buffer, handshakeCts.Token).ConfigureAwait(false);

                Reply selectReply = await ReadReplyAsync(conn, handshakeCts.Token)
                    .ConfigureAwait(false);
                if (selectReply is not { Kind: ReplyKind.SimpleString } || !IsOk(selectReply.Text))
                {
                    throw new RedisServerException($"Unexpected SELECT reply: {selectReply.Text}");
                }
            }

            return conn;
        }
        catch (Exception ex) when (IsTransient(ex, ct))
        {
            conn?.Dispose();
            tcp?.Dispose();
            _logger.LogWarning(
                ex,
                "Redis connection to {Host}:{Port} failed.",
                _options.Host,
                _options.Port
            );
            return null;
        }
        catch (Exception ex)
        {
            conn?.Dispose();
            tcp?.Dispose();
            if (ex is RedisServerException)
            {
                // Bad credentials / invalid database must fail visibly, not masquerade as perpetual misses.
                _logger.LogError(
                    ex,
                    "Redis connection handshake to {Host}:{Port} was rejected by the server.",
                    _options.Host,
                    _options.Port
                );
            }
            throw;
        }
    }

    private async Task<Stream> OpenTransportAsync(TcpClient tcp, CancellationToken ct)
    {
        NetworkStream network = tcp.GetStream();
        if (!_options.UseTls)
        {
            return network;
        }

        var ssl = new SslStream(network, leaveInnerStreamOpen: false);
        var authentication = new SslClientAuthenticationOptions
        {
            TargetHost = _options.TlsHost ?? _options.Host,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        };

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_options.ReadTimeout);
        try
        {
            await ssl.AuthenticateAsClientAsync(authentication, cts.Token).ConfigureAwait(false);
            return ssl;
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void Return(PooledConnection conn)
    {
        if (conn.IsConnected && _pool.Count < MaxPoolSize)
        {
            _pool.Add(conn);
        }
        else
        {
            conn.Dispose();
        }
    }

    private static async ValueTask WriteAsync(
        PooledConnection conn,
        ArrayBufferWriter<byte> buffer,
        CancellationToken ct
    )
    {
        await conn.Stream.WriteAsync(buffer.WrittenMemory, ct).ConfigureAwait(false);
        await conn.Stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static void AppendSetCommand(
        ArrayBufferWriter<byte> buffer,
        string redisKey,
        ReadOnlySpan<byte> value,
        long ttlMilliseconds
    )
    {
        bool withTtl = ttlMilliseconds > 0;
        AppendHeader(buffer, withTtl ? 5 : 3);
        AppendBulkString(buffer, "SET");
        AppendBulkString(buffer, redisKey);
        AppendBulkBytes(buffer, value);
        if (withTtl)
        {
            AppendBulkString(buffer, "PX");
            AppendBulkString(buffer, ttlMilliseconds.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void AppendHeader(ArrayBufferWriter<byte> buffer, int argumentCount)
    {
        Span<byte> head = buffer.GetSpan(1);
        head[0] = (byte)'*';
        buffer.Advance(1);
        AppendInteger(buffer, argumentCount);
        AppendCrlf(buffer);
    }

    private static void AppendBulkHeader(ArrayBufferWriter<byte> buffer, int length)
    {
        Span<byte> head = buffer.GetSpan(1);
        head[0] = (byte)'$';
        buffer.Advance(1);
        AppendInteger(buffer, length);
        AppendCrlf(buffer);
    }

    private static void AppendBulkString(ArrayBufferWriter<byte> buffer, string value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        AppendBulkHeader(buffer, byteCount);
        if (byteCount > 0)
        {
            buffer.Advance(Encoding.UTF8.GetBytes(value, buffer.GetSpan(byteCount)));
        }
        AppendCrlf(buffer);
    }

    private static void AppendBulkBytes(ArrayBufferWriter<byte> buffer, ReadOnlySpan<byte> value)
    {
        AppendBulkHeader(buffer, value.Length);
        if (!value.IsEmpty)
        {
            value.CopyTo(buffer.GetSpan(value.Length));
            buffer.Advance(value.Length);
        }
        AppendCrlf(buffer);
    }

    private static void AppendInteger(ArrayBufferWriter<byte> buffer, long value)
    {
        Span<byte> span = buffer.GetSpan(24);
        if (!value.TryFormat(span, out int written, provider: CultureInfo.InvariantCulture))
        {
            throw new InvalidOperationException("Could not format a Redis protocol integer.");
        }
        buffer.Advance(written);
    }

    private static void AppendCrlf(ArrayBufferWriter<byte> buffer)
    {
        Span<byte> span = buffer.GetSpan(2);
        span[0] = (byte)'\r';
        span[1] = (byte)'\n';
        buffer.Advance(2);
    }

    private static async ValueTask<Reply> ReadReplyAsync(
        PooledConnection conn,
        CancellationToken ct
    )
    {
        string header = await conn.ReadLineAsync(ct).ConfigureAwait(false);
        if (header.Length == 0)
        {
            conn.MarkDisconnected();
            throw new InvalidDataException("Empty Redis reply.");
        }

        ReadOnlySpan<char> rest = header.AsSpan(1);
        switch (header[0])
        {
            case '+':
                return Reply.Simple(header.Substring(1));

            case '-':
                // Server-side error: the connection is still usable, but the caller must see the failure.
                throw new RedisServerException(header.Substring(1));

            case ':':
                if (
                    !long.TryParse(
                        rest,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out long number
                    )
                )
                {
                    conn.MarkDisconnected();
                    throw new InvalidDataException($"Malformed Redis integer reply: '{header}'");
                }
                return Reply.FromInteger(number);

            case '$':
            {
                if (
                    !int.TryParse(
                        rest,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int length
                    )
                )
                {
                    conn.MarkDisconnected();
                    throw new InvalidDataException($"Malformed Redis bulk length: '{header}'");
                }
                if (length < 0)
                {
                    return Reply.Nil;
                }

                byte[] data = new byte[length];
                await conn.ReadExactAsync(data, ct).ConfigureAwait(false);
                await conn.ReadExactAsync(conn.CrlfScratch, ct).ConfigureAwait(false);
                return Reply.FromBulk(data);
            }

            case '*':
            {
                if (
                    !int.TryParse(
                        rest,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int count
                    )
                )
                {
                    conn.MarkDisconnected();
                    throw new InvalidDataException($"Malformed Redis array length: '{header}'");
                }
                if (count < 0)
                {
                    return Reply.Nil;
                }

                Reply[] items = new Reply[count];
                for (int i = 0; i < count; i++)
                {
                    items[i] = await ReadReplyAsync(conn, ct).ConfigureAwait(false);
                }
                return Reply.FromArray(items);
            }

            default:
                conn.MarkDisconnected();
                throw new InvalidDataException($"Unexpected Redis reply header: '{header}'");
        }
    }

    public void Dispose()
    {
        while (_pool.TryTake(out PooledConnection? conn))
        {
            conn.Dispose();
        }
    }

    private enum ReplyKind
    {
        SimpleString,
        Integer,
        Bulk,
        Array,
        Nil,
    }

    private sealed class Reply
    {
        public static readonly Reply Nil = new() { Kind = ReplyKind.Nil };

        public ReplyKind Kind { get; private init; }
        public string Text { get; private init; } = string.Empty;
        public long Integer { get; private init; }
        public byte[]? Value { get; private init; }
        public Reply[]? Items { get; private init; }

        public static Reply Simple(string text) =>
            new() { Kind = ReplyKind.SimpleString, Text = text };

        public static Reply FromInteger(long value) =>
            new() { Kind = ReplyKind.Integer, Integer = value };

        public static Reply FromBulk(byte[] value) =>
            new() { Kind = ReplyKind.Bulk, Value = value };

        public static Reply FromArray(Reply[] items) =>
            new() { Kind = ReplyKind.Array, Items = items };
    }

    private sealed class PooledConnection : IDisposable
    {
        private const int ReadBufferSize = 4096;
        private readonly TcpClient _tcp;
        private readonly byte[] _readBuffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        private int _start;
        private int _end;
        private bool _disconnected;
        private bool _bufferReturned;

        public Stream Stream { get; }
        public byte[] CrlfScratch { get; } = new byte[2];

        public bool IsConnected => !_disconnected && _tcp.Connected;

        public PooledConnection(TcpClient tcp, Stream stream)
        {
            _tcp = tcp;
            Stream = stream;
        }

        public void MarkDisconnected() => _disconnected = true;

        // Buffered CRLF line reader: reads chunks into a rented buffer and scans for '\n'.
        // Only lines longer than a whole buffer chunk allocate (a MemoryStream overflow).
        public async ValueTask<string> ReadLineAsync(CancellationToken ct)
        {
            MemoryStream? overflow = null;
            try
            {
                while (true)
                {
                    if (_start == _end && !await FillAsync(ct).ConfigureAwait(false))
                    {
                        MarkDisconnected();
                        throw new EndOfStreamException("Redis closed the connection.");
                    }

                    int idx = Array.IndexOf(_readBuffer, (byte)'\n', _start, _end - _start);
                    if (idx >= 0)
                    {
                        int len = idx - _start;
                        if (len > 0 && _readBuffer[idx - 1] == (byte)'\r')
                        {
                            len--;
                        }

                        string line;
                        if (overflow is null)
                        {
                            line = Encoding.UTF8.GetString(_readBuffer, _start, len);
                        }
                        else
                        {
                            overflow.Write(_readBuffer, _start, len);
                            byte[] arr = overflow.GetBuffer();
                            int total = (int)overflow.Length;
                            if (total > 0 && arr[total - 1] == (byte)'\r')
                            {
                                total--;
                            }
                            line = Encoding.UTF8.GetString(arr, 0, total);
                        }
                        _start = idx + 1;
                        return line;
                    }

                    // No LF in the buffered chunk: keep the raw bytes (a trailing '\r' may be
                    // completed by '\n' in the next chunk) and refill.
                    overflow ??= new MemoryStream((_end - _start) * 2);
                    overflow.Write(_readBuffer, _start, _end - _start);
                    _start = _end;
                }
            }
            finally
            {
                overflow?.Dispose();
            }
        }

        public async ValueTask ReadExactAsync(Memory<byte> destination, CancellationToken ct)
        {
            int buffered = Math.Min(destination.Length, _end - _start);
            if (buffered > 0)
            {
                _readBuffer.AsMemory(_start, buffered).CopyTo(destination);
                _start += buffered;
                destination = destination.Slice(buffered);
            }

            while (!destination.IsEmpty)
            {
                int read = await Stream.ReadAsync(destination, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    MarkDisconnected();
                    throw new EndOfStreamException("Redis closed the connection mid-reply.");
                }
                destination = destination.Slice(read);
            }
        }

        private async ValueTask<bool> FillAsync(CancellationToken ct)
        {
            _start = 0;
            _end = await Stream
                .ReadAsync(_readBuffer.AsMemory(0, ReadBufferSize), ct)
                .ConfigureAwait(false);
            return _end > 0;
        }

        public void Dispose()
        {
            _disconnected = true;
            if (!_bufferReturned)
            {
                _bufferReturned = true;
                ArrayPool<byte>.Shared.Return(_readBuffer);
            }
            try
            {
                Stream.Dispose();
            }
            catch { }
            try
            {
                _tcp.Dispose();
            }
            catch { }
        }
    }
}
