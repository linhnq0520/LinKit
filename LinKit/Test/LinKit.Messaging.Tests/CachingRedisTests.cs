using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LinKit.Caching;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace LinKit.Messaging.Tests;

public sealed class CachingRedisTests
{
    private static LinKitRedisOptions OptionsFor(FakeRedisServer server) =>
        new()
        {
            Host = "127.0.0.1",
            Port = server.Port,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            ReadTimeout = TimeSpan.FromSeconds(5),
        };

    [Fact]
    public async Task HybridL1_DefaultValuesAreHitsNotMisses()
    {
        using var cache = new HybridRedisCacheProvider(new LinKitRedisOptions { Enabled = false });
        await cache.SetAsync("zero", 0);
        await cache.SetAsync("flag", false);
        await cache.SetAsync("guid", Guid.Empty);
        await cache.SetAsync<string?>("nullable", null);

        var factoryCalls = 0;
        Assert.Equal(
            0,
            await cache.GetOrCreateAsync(
                "zero",
                _ =>
                {
                    factoryCalls++;
                    return Task.FromResult(42);
                }
            )
        );
        Assert.False(
            await cache.GetOrCreateAsync(
                "flag",
                _ =>
                {
                    factoryCalls++;
                    return Task.FromResult(true);
                }
            )
        );
        Assert.Equal(
            Guid.Empty,
            await cache.GetOrCreateAsync(
                "guid",
                _ =>
                {
                    factoryCalls++;
                    return Task.FromResult(Guid.NewGuid());
                }
            )
        );
        Assert.Null(
            await cache.GetOrCreateAsync<string?>(
                "nullable",
                _ =>
                {
                    factoryCalls++;
                    return Task.FromResult<string?>("created");
                }
            )
        );
        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public async Task HybridL2_DefaultValuesAreHitsNotMisses()
    {
        using var server = new FakeRedisServer();
        var options = OptionsFor(server);
        using var writer = new HybridRedisCacheProvider(options);
        await writer.SetAsync("zero", 0, TimeSpan.FromMinutes(10));
        await writer.SetAsync("flag", false, TimeSpan.FromMinutes(10));
        await writer.SetAsync<string?>("nullable", null, TimeSpan.FromMinutes(10));

        // A fresh provider has an empty L1, so these must come back from L2 as hits.
        using var reader = new HybridRedisCacheProvider(options);
        var factoryCalls = 0;
        Assert.Equal(
            0,
            await reader.GetOrCreateAsync(
                "zero",
                _ =>
                {
                    factoryCalls++;
                    return Task.FromResult(42);
                }
            )
        );
        Assert.False(
            await reader.GetOrCreateAsync(
                "flag",
                _ =>
                {
                    factoryCalls++;
                    return Task.FromResult(true);
                }
            )
        );
        Assert.Null(
            await reader.GetOrCreateAsync<string?>(
                "nullable",
                _ =>
                {
                    factoryCalls++;
                    return Task.FromResult<string?>("created");
                }
            )
        );
        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public async Task HybridL1Backfill_NeverOutlivesL2Ttl()
    {
        using var server = new FakeRedisServer();
        var time = new FakeTimeProvider();
        var options = OptionsFor(server);
        options.DefaultTtl = TimeSpan.FromMinutes(60);

        using var writer = new HybridRedisCacheProvider(options, time);
        await writer.SetAsync("key", "value", TimeSpan.FromMinutes(10));

        time.Advance(TimeSpan.FromMinutes(5));
        using var reader = new HybridRedisCacheProvider(options, time);
        Assert.Equal("value", await reader.GetAsync<string>("key")); // backfills L1 with the REMAINING 5 minutes

        time.Advance(TimeSpan.FromMinutes(4)); // 9 minutes total: L1 backfill must still be alive
        Assert.Equal("value", await reader.GetAsync<string>("key"));

        time.Advance(TimeSpan.FromMinutes(2)); // 11 minutes total: past the L2 TTL
        // With the old full-DefaultTtl (60 min) backfill, L1 would still serve the stale value here.
        Assert.Null(await reader.GetAsync<string>("key"));
    }

    [Fact]
    public async Task HybridFlight_ConcurrentSetIsNotOverwrittenByStaleFactoryResult()
    {
        using var cache = new HybridRedisCacheProvider(new LinKitRedisOptions { Enabled = false });
        var factoryStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var factoryGate = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        Task<string> flight = cache
            .GetOrCreateAsync(
                "key",
                async _ =>
                {
                    factoryStarted.TrySetResult();
                    return await factoryGate.Task;
                }
            )
            .AsTask();

        await factoryStarted.Task;
        await cache.SetAsync("key", "fresh");
        factoryGate.SetResult("stale");

        // The stale result is still returned to the callers that awaited the flight...
        Assert.Equal("stale", await flight);
        // ...but it must not overwrite the newer cached value.
        Assert.Equal("fresh", await cache.GetAsync<string>("key"));
    }

    [Fact]
    public async Task HybridFlight_ConcurrentRemoveIsNotResurrectedByFactoryResult()
    {
        using var cache = new HybridRedisCacheProvider(new LinKitRedisOptions { Enabled = false });
        var factoryStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var factoryGate = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        Task<string> flight = cache
            .GetOrCreateAsync(
                "key",
                async _ =>
                {
                    factoryStarted.TrySetResult();
                    return await factoryGate.Task;
                }
            )
            .AsTask();

        await factoryStarted.Task;
        await cache.RemoveAsync("key");
        factoryGate.SetResult("stale");
        Assert.Equal("stale", await flight);

        Assert.Null(await cache.GetAsync<string>("key"));
        Assert.Equal(
            "recreated",
            await cache.GetOrCreateAsync("key", _ => Task.FromResult("recreated"))
        );
    }

    [Fact]
    public async Task Resp2_WrongPasswordThrowsInsteadOfSilentMisses()
    {
        using var server = new FakeRedisServer { AuthError = "ERR invalid password" };
        var options = OptionsFor(server);
        options.Password = "wrong";
        using var client = new Resp2RedisClient(options);

        var ex = await Assert.ThrowsAsync<RedisServerException>(() => client.GetBytesAsync("key"));
        Assert.Contains("invalid password", ex.Message);
    }

    [Fact]
    public async Task Resp2_InvalidDatabaseThrows()
    {
        using var server = new FakeRedisServer { SelectError = "ERR DB index is out of range" };
        var options = OptionsFor(server);
        options.Database = 99;
        using var client = new Resp2RedisClient(options);

        await Assert.ThrowsAsync<RedisServerException>(() => client.GetBytesAsync("key"));
    }

    [Fact]
    public async Task Resp2_GetServerErrorThrows()
    {
        using var server = new FakeRedisServer
        {
            GetError = "WRONGTYPE Operation against a key holding the wrong kind of value",
        };
        using var client = new Resp2RedisClient(OptionsFor(server));

        var ex = await Assert.ThrowsAsync<RedisServerException>(() => client.GetBytesAsync("key"));
        Assert.StartsWith("WRONGTYPE", ex.Message);
    }

    [Fact]
    public async Task Resp2_LongErrorLineIsFullyParsed()
    {
        // Longer than the 4 KB read buffer: exercises the line-reader overflow path.
        var longMessage = "ERR " + new string('x', 10_000);
        using var server = new FakeRedisServer { GetError = longMessage };
        using var client = new Resp2RedisClient(OptionsFor(server));

        var ex = await Assert.ThrowsAsync<RedisServerException>(() => client.GetBytesAsync("key"));
        Assert.Equal(longMessage, ex.Message);
    }

    [Fact]
    public async Task Resp2_NilReplyIsAMiss()
    {
        using var server = new FakeRedisServer();
        using var client = new Resp2RedisClient(OptionsFor(server));
        Assert.Null(await client.GetBytesAsync("missing"));
    }

    [Fact]
    public async Task Resp2_SetGetDeleteRoundtrip()
    {
        using var server = new FakeRedisServer();
        using var client = new Resp2RedisClient(OptionsFor(server));
        byte[] value = Encoding.UTF8.GetBytes("hello redis");

        Assert.True(await client.SetBytesAsync("key", value, TimeSpan.FromMinutes(1)));
        Assert.Equal(value, await client.GetBytesAsync("key"));
        Assert.True(await client.DeleteKeyAsync("key"));
        Assert.Null(await client.GetBytesAsync("key"));
    }

    [Fact]
    public async Task Resp2_BufferedReaderHandlesChunkedRepliesAndLargeValues()
    {
        using var server = new FakeRedisServer { ChunkWrites = true };
        using var client = new Resp2RedisClient(OptionsFor(server));
        byte[] large = new byte[20_000]; // spans multiple 4 KB buffer fills
        new Random(42).NextBytes(large);

        Assert.True(await client.SetBytesAsync("big", large, TimeSpan.FromMinutes(1)));
        // Sequential gets reuse the pooled connection: leftover buffered bytes must be carried over.
        Assert.Equal(large, await client.GetBytesAsync("big"));
        Assert.Equal(large, await client.GetBytesAsync("big"));
        Assert.Null(await client.GetBytesAsync("missing"));
        Assert.Equal(large, await client.GetBytesAsync("big"));
    }

    [Fact]
    public async Task Resp2_HungServerHitsReadTimeoutInsteadOfStalling()
    {
        using var server = new FakeRedisServer { StallResponses = true };
        var options = OptionsFor(server);
        options.ReadTimeout = TimeSpan.FromMilliseconds(200);
        using var client = new Resp2RedisClient(options);

        var task = client.GetBytesAsync("key");
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(task, completed);
        Assert.Null(await task); // timeout is a logged miss, and the poisoned connection is dropped
    }

    [Fact]
    public void Resp2_InvalidOptionsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Resp2RedisClient(new LinKitRedisOptions { Port = 0 })
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Resp2RedisClient(new LinKitRedisOptions { ReadTimeout = TimeSpan.Zero })
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Resp2RedisClient(new LinKitRedisOptions { ConnectTimeout = TimeSpan.Zero })
        );
        Assert.Throws<ArgumentException>(() =>
            new Resp2RedisClient(new LinKitRedisOptions { Host = " " })
        );
        Assert.Throws<ArgumentException>(() =>
            new Resp2RedisClient(new LinKitRedisOptions { Username = "app" })
        );
    }

    [Fact]
    public async Task Resp2_AppliesKeyPrefixAndAclUser()
    {
        using var server = new FakeRedisServer();
        var options = OptionsFor(server);
        options.KeyPrefix = "app:";
        options.Username = "appuser";
        options.Password = "secret";
        using var client = new Resp2RedisClient(options);

        Assert.True(
            await client.SetBytesAsync("key", Encoding.UTF8.GetBytes("v"), TimeSpan.FromMinutes(1))
        );

        // The prefix lives on the wire only: callers keep using the unprefixed key.
        Assert.True(server.ContainsKey("app:key"));
        Assert.False(server.ContainsKey("key"));
        Assert.Equal("v", Encoding.UTF8.GetString((await client.GetBytesAsync("key"))!));
        Assert.Equal("appuser", server.AuthenticatedUser);
    }

    [Fact]
    public async Task Resp2_ExistsTtlExpireAndIncrement()
    {
        using var server = new FakeRedisServer();
        using var client = new Resp2RedisClient(OptionsFor(server));

        Assert.False(await client.ExistsAsync("counter"));
        Assert.Null(await client.TtlAsync("counter"));

        Assert.Equal(1L, await client.IncrementAsync("counter", ttl: TimeSpan.FromMinutes(1)));
        Assert.True(await client.ExistsAsync("counter"));
        Assert.NotNull(await client.TtlAsync("counter"));

        // A later increment must not restart the expiry window.
        Assert.Equal(6L, await client.IncrementAsync("counter", 5));
        Assert.True(await client.ExpireAsync("counter", TimeSpan.FromSeconds(30)));
        Assert.InRange((await client.TtlAsync("counter"))!.Value.TotalSeconds, 25, 30);
        Assert.False(await client.ExpireAsync("missing", TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task Resp2_GetManyAndSetManyRoundtrip()
    {
        using var server = new FakeRedisServer();
        using var client = new Resp2RedisClient(OptionsFor(server));

        var written = await client.SetManyAsync(
            new[]
            {
                new KeyValuePair<string, byte[]>("a", Encoding.UTF8.GetBytes("1")),
                new KeyValuePair<string, byte[]>("b", Encoding.UTF8.GetBytes("2")),
            },
            TimeSpan.FromMinutes(1)
        );
        Assert.Equal(2, written);

        IReadOnlyList<byte[]?> values = await client.GetManyAsync(new[] { "a", "b", "c" });
        Assert.Equal("1", Encoding.UTF8.GetString(values[0]!));
        Assert.Equal("2", Encoding.UTF8.GetString(values[1]!));
        Assert.Null(values[2]);
    }

    [Fact]
    public async Task Resp2_DeleteByPrefixScansAndDeletes()
    {
        using var server = new FakeRedisServer();
        var options = OptionsFor(server);
        options.KeyPrefix = "app:";
        using var client = new Resp2RedisClient(options);

        await client.SetBytesAsync(
            "orders:1",
            Encoding.UTF8.GetBytes("a"),
            TimeSpan.FromMinutes(1)
        );
        await client.SetBytesAsync(
            "orders:2",
            Encoding.UTF8.GetBytes("b"),
            TimeSpan.FromMinutes(1)
        );
        await client.SetBytesAsync("users:1", Encoding.UTF8.GetBytes("c"), TimeSpan.FromMinutes(1));

        Assert.Equal(2, await client.DeleteByPrefixAsync("orders:"));
        Assert.False(await client.ExistsAsync("orders:1"));
        Assert.True(await client.ExistsAsync("users:1"));
    }

    [Fact]
    public async Task HybridRemoveByPrefixClearsBothLevels()
    {
        using var server = new FakeRedisServer();
        var options = OptionsFor(server);
        using var writer = new HybridRedisCacheProvider(options);
        await writer.SetAsync("orders:1", "a");
        await writer.SetAsync("orders:2", "b");
        await writer.SetAsync("users:1", "c");

        Assert.True(await writer.RemoveByPrefixAsync("orders:") >= 2);

        // A fresh provider has an empty L1, so reading null proves L2 was purged as well.
        using var reader = new HybridRedisCacheProvider(options);
        Assert.Null(await reader.GetAsync<string>("orders:1"));
        Assert.Equal("c", await reader.GetAsync<string>("users:1"));
    }

    [Fact]
    public async Task Flight_OneCancelledCallerDoesNotCancelTheSharedFactory()
    {
        using var cache = new HybridRedisCacheProvider(new LinKitRedisOptions { Enabled = false });
        var factoryStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var gate = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var cancellation = new CancellationTokenSource();

        Task<string> cancelled = cache
            .GetOrCreateAsync(
                "key",
                async _ =>
                {
                    factoryStarted.TrySetResult();
                    return await gate.Task;
                },
                cancellationToken: cancellation.Token
            )
            .AsTask();

        await factoryStarted.Task;
        Task<string> survivor = cache
            .GetOrCreateAsync("key", _ => Task.FromResult("other"))
            .AsTask();

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        gate.SetResult("shared");
        Assert.Equal("shared", await survivor);
    }

    [Fact]
    public async Task Flight_CancelsTheFactoryOnceEveryWaiterGaveUp()
    {
        var cache = new InMemoryCacheProvider();
        var factoryStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var factoryCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var cancellation = new CancellationTokenSource();

        Task<string> caller = cache
            .GetOrCreateAsync(
                "key",
                async ct =>
                {
                    factoryStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.Infinite, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        factoryCancelled.TrySetResult();
                        throw;
                    }
                    return "never";
                },
                cancellationToken: cancellation.Token
            )
            .AsTask();

        await factoryStarted.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => caller);
        await factoryCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Minimal in-process RESP2 server (GET/SET/DEL/EXISTS/TTL/PEXPIRE/INCRBY/MGET/MSET/SCAN/AUTH/SELECT) so the
    /// client and the hybrid provider can be tested without a real Redis instance.
    /// </summary>
    private sealed class FakeRedisServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentDictionary<string, byte[]> _store = new();
        private readonly ConcurrentDictionary<string, long> _expiry = new();

        public string? AuthError { get; init; }
        public string? SelectError { get; init; }
        public string? GetError { get; init; }
        public bool StallResponses { get; init; }
        public bool ChunkWrites { get; init; }
        public string? AuthenticatedUser { get; private set; }
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public bool ContainsKey(string key) => TryRead(key, out _);

        public FakeRedisServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _ = AcceptLoopAsync();
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient tcp = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = HandleAsync(tcp);
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }

        private async Task HandleAsync(TcpClient tcp)
        {
            using TcpClient client = tcp;
            client.NoDelay = true;
            NetworkStream stream = client.GetStream();
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    string? header = await ReadLineAsync(stream, _cts.Token);
                    if (header is null || header.Length == 0 || header[0] != '*')
                    {
                        return;
                    }

                    int argc = int.Parse(header.AsSpan(1));
                    byte[][] args = new byte[argc][];
                    for (int i = 0; i < argc; i++)
                    {
                        string bulkHeader = (await ReadLineAsync(stream, _cts.Token))!;
                        int len = int.Parse(bulkHeader.AsSpan(1));
                        args[i] = new byte[len];
                        await ReadExactAsync(stream, args[i], _cts.Token);
                        await ReadExactAsync(stream, new byte[2], _cts.Token); // CRLF
                    }

                    if (StallResponses)
                    {
                        continue; // read commands but never answer: simulates a hung Redis
                    }

                    byte[] reply = BuildReply(args);
                    if (ChunkWrites)
                    {
                        // Force the client's buffered reader across many small network reads.
                        // No artificial delays: Windows timer resolution would make them dominate the read timeout.
                        for (int offset = 0; offset < reply.Length; offset += 7)
                        {
                            int count = Math.Min(7, reply.Length - offset);
                            await stream.WriteAsync(reply.AsMemory(offset, count), _cts.Token);
                            await stream.FlushAsync(_cts.Token);
                        }
                    }
                    else
                    {
                        await stream.WriteAsync(reply, _cts.Token);
                        await stream.FlushAsync(_cts.Token);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }

        private byte[] BuildReply(byte[][] args)
        {
            string command = Encoding.ASCII.GetString(args[0]).ToUpperInvariant();
            switch (command)
            {
                case "AUTH":
                    if (args.Length > 2)
                    {
                        AuthenticatedUser = Encoding.UTF8.GetString(args[1]);
                    }
                    return Ascii(AuthError is null ? "+OK\r\n" : $"-{AuthError}\r\n");
                case "SELECT":
                    return Ascii(SelectError is null ? "+OK\r\n" : $"-{SelectError}\r\n");
                case "GET":
                {
                    if (GetError is not null)
                    {
                        return Ascii($"-{GetError}\r\n");
                    }
                    string key = Encoding.UTF8.GetString(args[1]);
                    return TryRead(key, out byte[] value) ? Bulk(value) : Ascii("$-1\r\n");
                }
                case "SET":
                {
                    string key = Encoding.UTF8.GetString(args[1]);
                    _store[key] = args[2];
                    _expiry.TryRemove(key, out _);
                    for (int i = 3; i + 1 < args.Length; i += 2)
                    {
                        string option = Encoding.ASCII.GetString(args[i]).ToUpperInvariant();
                        if (
                            option == "PX"
                            && long.TryParse(Encoding.ASCII.GetString(args[i + 1]), out long ms)
                        )
                        {
                            _expiry[key] = Now + ms;
                        }
                    }
                    return Ascii("+OK\r\n");
                }
                case "EXISTS":
                    return Ascii(
                        TryRead(Encoding.UTF8.GetString(args[1]), out _) ? ":1\r\n" : ":0\r\n"
                    );
                case "TTL":
                {
                    string key = Encoding.UTF8.GetString(args[1]);
                    if (!TryRead(key, out _))
                    {
                        return Ascii(":-2\r\n");
                    }
                    if (!_expiry.TryGetValue(key, out long expiresAt))
                    {
                        return Ascii(":-1\r\n");
                    }
                    return Ascii($":{Math.Max(0, expiresAt - Now) / 1000}\r\n");
                }
                case "PEXPIRE":
                {
                    string key = Encoding.UTF8.GetString(args[1]);
                    if (!TryRead(key, out _))
                    {
                        return Ascii(":0\r\n");
                    }
                    _expiry[key] = Now + long.Parse(Encoding.ASCII.GetString(args[2]));
                    return Ascii(":1\r\n");
                }
                case "INCRBY":
                {
                    string key = Encoding.UTF8.GetString(args[1]);
                    long delta = long.Parse(Encoding.ASCII.GetString(args[2]));
                    long current =
                        TryRead(key, out byte[] existing)
                        && long.TryParse(Encoding.UTF8.GetString(existing), out long parsed)
                            ? parsed
                            : 0;
                    long next = current + delta;
                    _store[key] = Encoding.ASCII.GetBytes(next.ToString());
                    return Ascii($":{next}\r\n");
                }
                case "MGET":
                {
                    using var ms = new MemoryStream();
                    ms.Write(Ascii($"*{args.Length - 1}\r\n"));
                    for (int i = 1; i < args.Length; i++)
                    {
                        ms.Write(
                            TryRead(Encoding.UTF8.GetString(args[i]), out byte[] value)
                                ? Bulk(value)
                                : Ascii("$-1\r\n")
                        );
                    }
                    return ms.ToArray();
                }
                case "MSET":
                {
                    for (int i = 1; i + 1 < args.Length; i += 2)
                    {
                        string key = Encoding.UTF8.GetString(args[i]);
                        _store[key] = args[i + 1];
                        _expiry.TryRemove(key, out _);
                    }
                    return Ascii("+OK\r\n");
                }
                case "SCAN":
                {
                    string pattern = "*";
                    for (int i = 2; i + 1 < args.Length; i += 2)
                    {
                        if (
                            Encoding
                                .ASCII.GetString(args[i])
                                .Equals("MATCH", StringComparison.OrdinalIgnoreCase)
                        )
                        {
                            pattern = Encoding.UTF8.GetString(args[i + 1]);
                        }
                    }
                    string prefix = pattern.EndsWith('*') ? pattern[..^1] : pattern;
                    var matches = new List<string>();
                    foreach (string key in _store.Keys.OrderBy(k => k, StringComparer.Ordinal))
                    {
                        if (TryRead(key, out _) && key.StartsWith(prefix, StringComparison.Ordinal))
                        {
                            matches.Add(key);
                        }
                    }

                    using var ms = new MemoryStream();
                    ms.Write(Ascii("*2\r\n"));
                    ms.Write(Ascii("$1\r\n0\r\n")); // single page: cursor is always "0"
                    ms.Write(Ascii($"*{matches.Count}\r\n"));
                    foreach (string key in matches)
                    {
                        ms.Write(Bulk(Encoding.UTF8.GetBytes(key)));
                    }
                    return ms.ToArray();
                }
                case "DEL":
                {
                    int removed = 0;
                    for (int i = 1; i < args.Length; i++)
                    {
                        string key = Encoding.UTF8.GetString(args[i]);
                        if (TryRead(key, out _) && _store.TryRemove(key, out _))
                        {
                            removed++;
                        }
                        _expiry.TryRemove(key, out _);
                    }
                    return Ascii($":{removed}\r\n");
                }
                default:
                    return Ascii($"-ERR unknown command '{command}'\r\n");
            }
        }

        private static long Now => Environment.TickCount64;

        private bool TryRead(string key, out byte[] value)
        {
            if (_expiry.TryGetValue(key, out long expiresAt) && expiresAt <= Now)
            {
                _store.TryRemove(key, out _);
                _expiry.TryRemove(key, out _);
                value = Array.Empty<byte>();
                return false;
            }
            return _store.TryGetValue(key, out value!);
        }

        private static byte[] Bulk(byte[] value)
        {
            using var ms = new MemoryStream();
            ms.Write(Ascii($"${value.Length}\r\n"));
            ms.Write(value);
            ms.Write(Ascii("\r\n"));
            return ms.ToArray();
        }

        private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

        private static async Task<string?> ReadLineAsync(NetworkStream stream, CancellationToken ct)
        {
            using var buffer = new MemoryStream();
            byte[] one = new byte[1];
            while (true)
            {
                int read = await stream.ReadAsync(one, ct);
                if (read == 0)
                {
                    return null;
                }
                if (one[0] == (byte)'\n')
                {
                    byte[] bytes = buffer.ToArray();
                    int len = bytes.Length;
                    if (len > 0 && bytes[len - 1] == (byte)'\r')
                    {
                        len--;
                    }
                    return Encoding.ASCII.GetString(bytes, 0, len);
                }
                buffer.WriteByte(one[0]);
            }
        }

        private static async Task ReadExactAsync(
            NetworkStream stream,
            byte[] buffer,
            CancellationToken ct
        )
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(offset), ct);
                if (read == 0)
                {
                    throw new EndOfStreamException();
                }
                offset += read;
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }
    }
}
