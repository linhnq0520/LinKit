using LinKit.Caching;
using LinKit.Core.Resilience;
using LinKit.Data;
using LinKit.Data.Dialects;
using LinKit.Messaging.Kafka;
using LinKit.Outbox;
using LinKit.Resilience;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace LinKit.Messaging.Tests;

public sealed class ReliabilityTests
{
    [Fact]
    public async Task CacheRemovesFaultedFactoryAfterAllWaitersCancel()
    {
        var cache = new InMemoryCacheProvider();
        var factoryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failFactory = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();

        var cancelledCaller = cache.GetOrCreateAsync(
            "faulted-cache-entry",
            async _ =>
            {
                factoryStarted.TrySetResult();
                return await failFactory.Task;
            },
            cancellationToken: cancellation.Token).AsTask();

        await factoryStarted.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledCaller);
        failFactory.SetException(new InvalidOperationException("factory failed"));

        var recovered = false;
        for (var attempt = 0; attempt < 100 && !recovered; attempt++)
        {
            try
            {
                var value = await cache.GetOrCreateAsync(
                    "faulted-cache-entry",
                    _ => Task.FromResult("recovered"));
                Assert.Equal("recovered", value);
                recovered = true;
            }
            catch (InvalidOperationException) when (attempt < 99)
            {
                await Task.Yield();
            }
        }

        Assert.True(recovered);
    }

    [Fact]
    public void OracleMetadataQueriesPreserveQuotedIdentifiersAndSchema()
    {
        var dialect = new OracleDialect();
        var table = new TableSchemaMetadata
        {
            TableName = "Order'Summary",
            Schema = "Tenant'One",
            EntityType = typeof(ReliabilityTests),
            Columns = Array.Empty<ColumnSchemaMetadata>()
        };

        Assert.Equal(
            "SELECT COLUMN_NAME FROM ALL_TAB_COLS WHERE OWNER = 'Tenant''One' AND TABLE_NAME = 'Order''Summary';",
            dialect.GenerateGetExistingColumnsSql(table));
        Assert.Equal(
            "SELECT INDEX_NAME FROM ALL_INDEXES WHERE OWNER = 'Tenant''One' AND TABLE_NAME = 'Order''Summary';",
            dialect.GenerateGetExistingIndexesSql(table));
    }

    [Fact]
    public async Task OutboxClaimHidesMessagesFromOtherPublishersUntilLeaseExpires()
    {
        var time = new FakeTimeProvider();
        var store = new InMemoryOutboxStore(new OutboxOptions(), time);
        await ((IOutboxStore)store).SaveMessageAsync(new OutboxMessage { MessageType = "A", Payload = "{}" });

        var first = await store.ClaimPendingAsync(10, TimeSpan.FromSeconds(30));
        var second = await store.ClaimPendingAsync(10, TimeSpan.FromSeconds(30));
        time.Advance(TimeSpan.FromSeconds(31));
        var afterLease = await store.ClaimPendingAsync(10, TimeSpan.FromSeconds(30));

        Assert.Single(first);
        Assert.Empty(second);
        Assert.Single(afterLease);
    }

    [Fact]
    public async Task OutboxBackoffAndDeadLetter()
    {
        var time = new FakeTimeProvider();
        var options = new OutboxOptions { MaxRetries = 2, BaseRetryDelay = TimeSpan.FromSeconds(10) };
        var store = new InMemoryOutboxStore(options, time);
        var msg = new OutboxMessage { MessageType = "A", Payload = "{}" };
        await ((IOutboxStore)store).SaveMessageAsync(msg);

        await ((IOutboxStore)store).MarkFailedAsync(msg.Id, "boom", time.GetUtcNow().UtcDateTime + options.GetRetryDelay(1));
        Assert.Empty(await store.ClaimPendingAsync(10, TimeSpan.FromSeconds(1)));

        time.Advance(TimeSpan.FromSeconds(11));
        Assert.Single(await store.ClaimPendingAsync(10, TimeSpan.FromSeconds(1)));

        await ((IOutboxStore)store).MarkFailedAsync(msg.Id, "boom", time.GetUtcNow().UtcDateTime);
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.Empty(await store.ClaimPendingAsync(10, TimeSpan.FromSeconds(1)));
        Assert.Single(store.GetDeadLettered());
    }

    [Fact]
    public void OutboxRetryDelayIsExponentialAndCapped()
    {
        var o = new OutboxOptions { BaseRetryDelay = TimeSpan.FromSeconds(2), MaxRetryDelay = TimeSpan.FromSeconds(30) };
        Assert.Equal(TimeSpan.FromSeconds(2), o.GetRetryDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(8), o.GetRetryDelay(3));
        Assert.Equal(TimeSpan.FromSeconds(30), o.GetRetryDelay(50));
    }

    [Fact]
    public void OutboxOptionsRejectInvalidValuesAndRetryDelayDoesNotOverflow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OutboxOptions { BatchSize = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new OutboxOptions { LeaseDuration = TimeSpan.Zero }.Validate());

        var largeDelay = TimeSpan.FromDays(100_000);
        var options = new OutboxOptions { BaseRetryDelay = largeDelay, MaxRetryDelay = TimeSpan.MaxValue };
        Assert.Equal(TimeSpan.MaxValue, options.GetRetryDelay(10));
        Assert.Equal(TimeSpan.Zero, new OutboxOptions
        {
            BaseRetryDelay = TimeSpan.Zero,
            MaxRetryDelay = TimeSpan.FromTicks(1)
        }.GetRetryDelay(20));
    }

    [Fact]
    public async Task KafkaDeadLettersAfterMaxAttemptsAndCommits()
    {
        var commits = 0;
        var seeks = 0;
        var dead = 0;

        await KafkaRecordProcessor.ProcessAsync(
            () => throw new InvalidOperationException("poison"),
            () => commits++,
            () => seeks++,
            "t", "k", TimeSpan.Zero, CancellationToken.None, null,
            attempt: 3, maxAttempts: 3, deadLetter: _ => { dead++; return Task.CompletedTask; });

        Assert.Equal(1, dead);
        Assert.Equal(1, commits);
        Assert.Equal(0, seeks);
    }

    [Fact]
    public async Task KafkaSeeksWhenDeadLetterFails()
    {
        var commits = 0;
        var seeks = 0;

        await KafkaRecordProcessor.ProcessAsync(
            () => throw new InvalidOperationException("poison"),
            () => commits++,
            () => seeks++,
            "t", "k", TimeSpan.Zero, CancellationToken.None, null,
            attempt: 3, maxAttempts: 3, deadLetter: _ => throw new InvalidOperationException("dlq down"));

        Assert.Equal(0, commits);
        Assert.Equal(1, seeks);
    }

    [Fact]
    public async Task ResilienceRetriesThenSucceeds()
    {
        int calls = 0;
        var opts = new ResilienceOptions { MaxRetries = 3, RetryDelay = TimeSpan.Zero };

        var result = await ResiliencePipeline.ExecuteAsync("retry-ok", ct =>
        {
            if (++calls < 3) throw new InvalidOperationException();
            return Task.FromResult(42);
        }, opts);

        Assert.Equal(42, result);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task ResilienceRejectsInvalidOptionsBeforeInvokingAction()
    {
        var calls = 0;
        var options = new ResilienceOptions { Timeout = TimeSpan.FromMilliseconds(-1) };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            ResiliencePipeline.ExecuteAsync("invalid-options", _ =>
            {
                calls++;
                return Task.FromResult(1);
            }, options));

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ResilienceTimeoutThrowsTimeoutException()
    {
        var opts = new ResilienceOptions { Timeout = TimeSpan.FromMilliseconds(50) };
        await Assert.ThrowsAsync<TimeoutException>(() =>
            ResiliencePipeline.ExecuteAsync<int>("timeout", async ct =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return 1;
            }, opts));
    }

    [Fact]
    public async Task ResilienceDoesNotRetryCallerCancellation()
    {
        using var cts = new CancellationTokenSource();
        int calls = 0;
        var opts = new ResilienceOptions { MaxRetries = 5, RetryDelay = TimeSpan.Zero };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ResiliencePipeline.ExecuteAsync<int>("cancel", ct =>
            {
                calls++;
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(1);
            }, opts, cts.Token));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RejectedCallsDoNotExtendOpenCircuit()
    {
        var time = new FakeTimeProvider();
        var opts = new ResilienceOptions { BreakerFailureThreshold = 1, BreakerDurationSeconds = 10 };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ResiliencePipeline.ExecuteAsync<int>("cb-no-extend", _ => throw new InvalidOperationException(), opts, default, time));
        for (var i = 0; i < 4; i++)
        {
            time.Advance(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAsync<CircuitBreakerOpenException>(() =>
                ResiliencePipeline.ExecuteAsync("cb-no-extend", _ => Task.FromResult(1), opts, default, time));
        }

        time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(1, await ResiliencePipeline.ExecuteAsync("cb-no-extend", _ => Task.FromResult(1), opts, default, time));
    }

    [Fact]
    public void TenantConsumeInterceptorSetsAndRestoresTenant()
    {
        var accessor = new LinKit.Tenancy.TenantAccessor();
        accessor.Context.TenantId = "outer";
        var interceptor = new LinKit.Tenancy.TenantConsumeInterceptor(accessor);
        var headers = new Dictionary<string, object> { ["linkit-tenant-id"] = "tenant-a" };

        using (LinKit.Core.Messaging.MessageConsumeInterceptors.Enter(
                   new ServiceCollection().AddSingleton<LinKit.Core.Messaging.IMessageConsumeInterceptor>(interceptor).BuildServiceProvider(),
                   headers))
        {
            Assert.Equal("tenant-a", accessor.Context.TenantId);
        }

        Assert.Equal("outer", accessor.Context.TenantId);
    }

    [Fact]
    public async Task CircuitBreakerOpensThenHalfOpensWithFakeTime()
    {
        var time = new FakeTimeProvider();
        var opts = new ResilienceOptions { BreakerFailureThreshold = 2, BreakerDurationSeconds = 10 };
        Task<int> Fail(CancellationToken _) => throw new InvalidOperationException();

        await Assert.ThrowsAsync<InvalidOperationException>(() => ResiliencePipeline.ExecuteAsync("cb", Fail, opts, default, time));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ResiliencePipeline.ExecuteAsync("cb", Fail, opts, default, time));
        await Assert.ThrowsAsync<CircuitBreakerOpenException>(() => ResiliencePipeline.ExecuteAsync("cb", Fail, opts, default, time));

        time.Advance(TimeSpan.FromSeconds(11));
        var ok = await ResiliencePipeline.ExecuteAsync("cb", _ => Task.FromResult(7), opts, default, time);
        Assert.Equal(7, ok);
        Assert.Equal(CircuitState.Closed, CircuitBreakerRegistry.GetOrCreate("cb").State);
    }

    [Fact]
    public void HalfOpenProbeThatNeverReportsDoesNotWedgeBreaker()
    {
        var time = new FakeTimeProvider();
        var cb = new CircuitBreakerState(time);
        cb.OnFailure(1);
        time.Advance(TimeSpan.FromSeconds(11));
        Assert.True(cb.CanExecute(10));
        Assert.False(cb.CanExecute(10));
        time.Advance(TimeSpan.FromSeconds(11));
        Assert.True(cb.CanExecute(10));
    }

    [Fact]
    public async Task CacheSingleFlightSharesOneFactoryAcrossManyConcurrentCallersAndRecoversAfterFault()
    {
        var cache = new InMemoryCacheProvider();
        var calls = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<int> Factory(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            await release.Task;
            throw new InvalidOperationException("boom");
        }

        var callers = Enumerable.Range(0, 50)
            .Select(_ => Task.Run(() => cache.GetOrCreateAsync("shared", Factory).AsTask()))
            .ToArray();
        await Task.Delay(50);
        release.SetResult();

        foreach (var caller in callers)
            await Assert.ThrowsAsync<InvalidOperationException>(() => caller);
        Assert.Equal(1, calls);

        Assert.Equal(5, await cache.GetOrCreateAsync("shared", _ => Task.FromResult(5)));
    }

    [Fact]
    public async Task CacheSingleFlightRunsFactoryOnce()
    {
        var cache = new InMemoryCacheProvider();
        int calls = 0;
        async Task<int> Factory(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(100);
            return 5;
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => cache.GetOrCreateAsync("k", Factory).AsTask()));

        Assert.All(results, r => Assert.Equal(5, r));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CacheHardCapEvictsLeastRecentlyUsed()
    {
        var time = new FakeTimeProvider();
        var cache = new InMemoryCacheProvider(10, time);
        for (int i = 0; i < 100; i++)
        {
            await cache.SetAsync($"k{i}", i);
            time.Advance(TimeSpan.FromMilliseconds(1));
        }

        Assert.True(cache.Count <= 10);
        Assert.Equal(99, await cache.GetAsync<int>("k99"));
        Assert.Equal(0, await cache.GetAsync<int>("k0"));
    }

    [Fact]
    public async Task CacheExpiresWithFakeTime()
    {
        var time = new FakeTimeProvider();
        var cache = new InMemoryCacheProvider(100, time);
        await cache.SetAsync("a", "v", TimeSpan.FromSeconds(5));
        Assert.Equal("v", await cache.GetAsync<string>("a"));
        time.Advance(TimeSpan.FromSeconds(6));
        Assert.Null(await cache.GetAsync<string>("a"));
    }
}
