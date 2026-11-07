using LinKit.Data;
using LinKit.Outbox;
using LinKit.Outbox.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace LinKit.Messaging.Tests;

public sealed class InboxClaimTests
{
    [Fact]
    public async Task InMemoryFirstClaimWinsAndSecondIsRejected()
    {
        var store = new InMemoryInboxStore();

        Assert.True(await store.TryMarkProcessedAsync("m-1", "consumer-a"));
        Assert.False(await store.TryMarkProcessedAsync("m-1", "consumer-a"));
        Assert.True(await store.HasBeenProcessedAsync("m-1", "consumer-a"));

        // Claims are scoped per consumer.
        Assert.True(await store.TryMarkProcessedAsync("m-1", "consumer-b"));
        Assert.False(await store.TryMarkProcessedAsync("m-1", "consumer-b"));
    }

    [Fact]
    public async Task InMemoryConcurrentClaimsHaveExactlyOneWinner()
    {
        var store = new InMemoryInboxStore();
        using var gate = new ManualResetEventSlim(false);

        var claims = Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
        {
            gate.Wait();
            return store.TryMarkProcessedAsync("m-1", "consumer-a");
        })).ToArray();

        gate.Set();
        var results = await Task.WhenAll(claims);

        Assert.Equal(1, results.Count(r => r));
    }

    [Fact]
    public async Task InMemoryExpiredClaimCanBeReclaimed()
    {
        var store = new InMemoryInboxStore();

        Assert.True(await store.TryMarkProcessedAsync("m-1", "consumer-a", TimeSpan.Zero));
        // A zero-length expiry is immediately expired, matching HasBeenProcessedAsync semantics.
        Assert.False(await store.HasBeenProcessedAsync("m-1", "consumer-a"));
        Assert.True(await store.TryMarkProcessedAsync("m-1", "consumer-a"));

        // A live claim without expiry is never reclaimable.
        Assert.False(await store.TryMarkProcessedAsync("m-1", "consumer-a", TimeSpan.Zero));
    }

    [Fact]
    public async Task AdoNetClaimIsInsertOnlyAndHonorsExpiry()
    {
        var connectionString = $"Data Source=inbox-claim-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        var time = new FakeTimeProvider();
        var factory = new DelegateDbConnectionFactory(DatabaseDialect.Sqlite, () => new SqliteConnection(connectionString));
        await using var unitOfWork = new UnitOfWork(factory);
        var inbox = new AdoNetInboxStore(factory, unitOfWork, time);
        await inbox.EnsureSchemaAsync();

        Assert.True(await inbox.TryMarkProcessedAsync("m-1", "consumer-a", TimeSpan.FromSeconds(5)));
        Assert.False(await inbox.TryMarkProcessedAsync("m-1", "consumer-a", TimeSpan.FromSeconds(5)));
        Assert.True(await inbox.HasBeenProcessedAsync("m-1", "consumer-a"));
        Assert.True(await inbox.TryMarkProcessedAsync("m-1", "consumer-b"));

        time.Advance(TimeSpan.FromSeconds(6));
        Assert.False(await inbox.HasBeenProcessedAsync("m-1", "consumer-a"));
        Assert.True(await inbox.TryMarkProcessedAsync("m-1", "consumer-a"));
        Assert.False(await inbox.TryMarkProcessedAsync("m-1", "consumer-a"));

        // A claim recorded via MarkProcessedAsync also blocks TryMarkProcessedAsync.
        await inbox.MarkProcessedAsync("m-2", "consumer-a");
        Assert.False(await inbox.TryMarkProcessedAsync("m-2", "consumer-a"));

        // A claim without expiry never expires.
        Assert.True(await inbox.TryMarkProcessedAsync("m-3", "consumer-a"));
        time.Advance(TimeSpan.FromDays(30));
        Assert.False(await inbox.TryMarkProcessedAsync("m-3", "consumer-a"));
    }

    [Fact]
    public async Task AdoNetConcurrentClaimsHaveExactlyOneWinner()
    {
        // A file-backed database is used because shared-cache in-memory SQLite returns
        // SQLITE_LOCKED for concurrent writers instead of honoring the busy timeout.
        var databasePath = Path.Combine(Path.GetTempPath(), $"linkit-inbox-claim-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Pooling=false";
        try
        {
            var factory = new DelegateDbConnectionFactory(DatabaseDialect.Sqlite, () => new SqliteConnection(connectionString));
            await using var unitOfWork = new UnitOfWork(factory);
            var inbox = new AdoNetInboxStore(factory, unitOfWork);
            await inbox.EnsureSchemaAsync();
            using var gate = new ManualResetEventSlim(false);

            var claims = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
            {
                gate.Wait();
                return await inbox.TryMarkProcessedAsync("m-1", "consumer-a");
            })).ToArray();

            gate.Set();
            var results = await Task.WhenAll(claims);

            Assert.Equal(1, results.Count(r => r));
            Assert.True(await inbox.HasBeenProcessedAsync("m-1", "consumer-a"));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task AdoNetClaimRejectsInvalidArguments()
    {
        var connectionString = $"Data Source=inbox-claim-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        var factory = new DelegateDbConnectionFactory(DatabaseDialect.Sqlite, () => new SqliteConnection(connectionString));
        await using var unitOfWork = new UnitOfWork(factory);
        var inbox = new AdoNetInboxStore(factory, unitOfWork);
        await inbox.EnsureSchemaAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => inbox.TryMarkProcessedAsync(" ", "consumer-a"));
        await Assert.ThrowsAsync<ArgumentException>(() => inbox.TryMarkProcessedAsync("m-1", " "));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => inbox.TryMarkProcessedAsync("m-1", "consumer-a", TimeSpan.FromSeconds(-1)));
    }
}