using System.Data;
using LinKit.Data;
using LinKit.Data.Dialects;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LinKit.Messaging.Tests;

/// <summary>
/// Covers the Phase 1.6 contract of <see cref="DataSession"/>: the concurrency gate, the configurable command
/// timeout, the default isolation level and the idempotent-command policy seam.
/// </summary>
public sealed class DataSessionConcurrencyTests
{
    private static string MemoryConnectionString(string prefix) =>
        $"Data Source={prefix}-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

    [Fact]
    public async Task ConcurrentOperationsThrowByDefault()
    {
        var connectionString = MemoryConnectionString("session-strict");
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();

        await using var session = new DataSession(
            () => new SqliteConnection(connectionString),
            new SqlDialect(DatabaseKind.Sqlite)
        );

        // An in-flight stream holds the session; a second operation must fail fast instead of corrupting state.
        await using var rows = session
            .StreamAsync(
                new CommandSpec("SELECT 1", Array.Empty<SqlArgument>()),
                reader => reader.GetInt64(0)
            )
            .GetAsyncEnumerator();
        Assert.True(await rows.MoveNextAsync());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.ScalarSqlAsync("SELECT 1")
        );
        Assert.Contains("Concurrent operations on one DataSession", error.Message);
    }

    [Fact]
    public async Task ConcurrentOperationsAreSerializedWhenEnabled()
    {
        var connectionString = MemoryConnectionString("session-gate");
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();

        await using var session = new DataSession(
            () => new SqliteConnection(connectionString),
            new SqlDialect(DatabaseKind.Sqlite)
        )
        {
            AllowConcurrentOperations = true,
        };

        await using var rows = session
            .StreamAsync(
                new CommandSpec("SELECT 1", Array.Empty<SqlArgument>()),
                reader => reader.GetInt64(0)
            )
            .GetAsyncEnumerator();
        Assert.True(await rows.MoveNextAsync());

        // Queued behind the stream rather than rejected.
        Task<object?> queued = session.ScalarSqlAsync("SELECT 1");
        Assert.False(queued.IsCompleted);

        await rows.DisposeAsync(); // releases the gate
        Assert.Equal(1L, Convert.ToInt64(await queued.WaitAsync(TimeSpan.FromSeconds(5))));
    }

    [Fact]
    public async Task CommandTimeoutComesFromTheSession()
    {
        var configured = -1;
        await using var session = new DataSession(
            () => new SqliteConnection(MemoryConnectionString("session-timeout")),
            new SqlDialect(DatabaseKind.Sqlite),
            configureCommand: command =>
            {
                configured = command.CommandTimeout;
                return default;
            }
        )
        {
            CommandTimeout = 7,
        };

        await session.ScalarSqlAsync("SELECT 1");
        Assert.Equal(7, configured);

        await using var defaulted = new DataSession(
            () => new SqliteConnection(MemoryConnectionString("session-timeout-default")),
            new SqlDialect(DatabaseKind.Sqlite),
            configureCommand: command =>
            {
                configured = command.CommandTimeout;
                return default;
            }
        );

        await defaulted.ScalarSqlAsync("SELECT 1");
        Assert.Equal(LinKitDataOptions.DefaultCommandTimeout, configured);
    }

    [Fact]
    public async Task OptionsFlowIntoTheSession()
    {
        var services = new ServiceCollection();
        services.AddLinKitDataCore(options =>
            options
                .UseConnection(
                    DatabaseDialect.Sqlite,
                    () => new SqliteConnection(MemoryConnectionString("session-options"))
                )
                .UseBatchSize(250)
                .UseCommandTimeout(11)
                .UseTransactionIsolation(IsolationLevel.ReadCommitted)
                .UseConcurrentOperations()
        );

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var session = scope.ServiceProvider.GetRequiredService<DataSession>();

        Assert.Equal(250, session.BatchSize);
        Assert.Equal(11, session.CommandTimeout);
        Assert.Equal(IsolationLevel.ReadCommitted, session.DefaultIsolationLevel);
        Assert.True(session.AllowConcurrentOperations);
    }

    [Fact]
    public async Task TransientExecutorRetriesOnlyTransientFailures()
    {
        var executor = new TransientFailureExecutor(
            static _ => true,
            maxRetries: 3,
            delayFactory: static _ => TimeSpan.Zero
        );

        var attempts = 0;
        var value = await executor.ExecuteAsync(
            (CancellationToken _) =>
            {
                attempts++;
                return attempts < 3
                    ? Task.FromException<int>(new TimeoutException())
                    : Task.FromResult(42);
            },
            CancellationToken.None
        );

        Assert.Equal(42, value);
        Assert.Equal(3, attempts);

        var strict = new TransientFailureExecutor(
            static _ => false,
            maxRetries: 3,
            delayFactory: static _ => TimeSpan.Zero
        );

        attempts = 0;
        await Assert.ThrowsAsync<TimeoutException>(() =>
            strict.ExecuteAsync<int>(
                (CancellationToken _) =>
                {
                    attempts++;
                    return Task.FromException<int>(new TimeoutException());
                },
                CancellationToken.None
            )
        );
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task QueriesGoThroughTheExecutorButMutationsDoNot()
    {
        var connectionString = MemoryConnectionString("session-executor");
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();

        var executor = new CountingExecutor();
        await using var session = new DataSession(
            () => new SqliteConnection(connectionString),
            new SqlDialect(DatabaseKind.Sqlite)
        )
        {
            IdempotentExecutor = executor,
        };

        await session.QuerySqlAsync("SELECT 1", reader => reader.GetInt64(0));
        Assert.Equal(1, executor.Calls);

        await session.ScalarSqlAsync("SELECT 1");
        Assert.Equal(1, executor.Calls); // ScalarAsync is used by identity inserts, so it is never replayed

        await session.ExecuteSqlAsync("CREATE TABLE executor_probe (Id INTEGER PRIMARY KEY)");
        Assert.Equal(1, executor.Calls); // mutations are never replayed

        await session.ExecuteIdempotentAsync(
            new CommandSpec("DROP TABLE executor_probe", Array.Empty<SqlArgument>())
        );
        Assert.Equal(2, executor.Calls);
    }

    private sealed class CountingExecutor : IDataOperationExecutor
    {
        public int Calls { get; private set; }

        public Task<T> ExecuteAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken
        )
        {
            Calls++;
            return operation(cancellationToken);
        }
    }
}
