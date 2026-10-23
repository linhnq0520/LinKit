using LinKit.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LinKit.Messaging.Tests;

public sealed class UnitOfWorkInterceptorTests
{
    [Fact]
    public async Task CommitInvokesHooksInRegistrationOrderAroundTransaction()
    {
        var database = await TestDatabase.CreateAsync();
        await using var unitOfWork = new UnitOfWork(
            database.Factory,
            new IUnitOfWorkInterceptor[] { new RecordingInterceptor("first", database.Events), new RecordingInterceptor("second", database.Events) });

        await unitOfWork.BeginTransactionAsync();
        await unitOfWork.CommitAsync();

        Assert.Equal(
            new[] { "first.before", "second.before", "first.commit", "second.commit" },
            database.Events);
    }

    [Fact]
    public async Task ScopedDependencyInjectionResolvesRegisteredInterceptors()
    {
        var database = await TestDatabase.CreateAsync();
        var services = new ServiceCollection();
        services.AddSingleton<IDbConnectionFactory>(database.Factory);
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IUnitOfWorkInterceptor>(_ => new RecordingInterceptor("registered", database.Events));

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await unitOfWork.BeginTransactionAsync();
        await unitOfWork.CommitAsync();

        Assert.Equal(new[] { "registered.before", "registered.commit" }, database.Events);
    }

    [Fact]
    public async Task BeforeCommitFailureRollsBackAndNotifiesRollbackListeners()
    {
        var database = await TestDatabase.CreateAsync();
        var veto = new VetoInterceptor(database.Events);
        await using var unitOfWork = new UnitOfWork(
            database.Factory,
            new IUnitOfWorkInterceptor[] { veto, new RecordingInterceptor("next", database.Events) });

        var transaction = await unitOfWork.BeginTransactionAsync();
        await using (var command = transaction.Connection!.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "CREATE TABLE vetoed (id INTEGER PRIMARY KEY)";
            await command.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.CommitAsync());

        Assert.Equal("commit vetoed", exception.Message);
        Assert.Equal(new[] { "veto.before", "next.rollback" }, database.Events);
        await using var verifyConnection = new SqliteConnection(database.ConnectionString);
        await verifyConnection.OpenAsync();
        await using var verifyCommand = verifyConnection.CreateCommand();
        verifyCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'vetoed'";
        Assert.Equal(0L, await verifyCommand.ExecuteScalarAsync());
    }

    [Fact]
    public async Task AfterCommitFailureIsReportedWithoutUndoingCommitOrSkippingOtherListeners()
    {
        var database = await TestDatabase.CreateAsync();
        await using var unitOfWork = new UnitOfWork(
            database.Factory,
            new IUnitOfWorkInterceptor[] { new FailingAfterCommitInterceptor(database.Events), new RecordingInterceptor("next", database.Events) });

        var transaction = await unitOfWork.BeginTransactionAsync();
        await using (var command = transaction.Connection!.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "CREATE TABLE committed (id INTEGER PRIMARY KEY)";
            await command.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<UnitOfWorkHookException>(() => unitOfWork.CommitAsync());

        Assert.True(exception.TransactionCommitted);
        Assert.Equal("AfterCommit", exception.HookName);
        Assert.Equal(new[] { "next.before", "first.commit", "next.commit" }, database.Events);
        await using var verifyConnection = new SqliteConnection(database.ConnectionString);
        await verifyConnection.OpenAsync();
        await using var verifyCommand = verifyConnection.CreateCommand();
        verifyCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'committed'";
        Assert.Equal(1L, await verifyCommand.ExecuteScalarAsync());
    }

    private sealed class RecordingInterceptor(string name, List<string> events) : IUnitOfWorkInterceptor
    {
        public Task BeforeCommitAsync(
            System.Data.Common.DbConnection connection,
            System.Data.Common.DbTransaction transaction,
            CancellationToken cancellationToken = default)
        {
            events.Add($"{name}.before");
            Assert.Same(connection, transaction.Connection);
            return Task.CompletedTask;
        }

        public Task AfterCommitAsync(System.Data.Common.DbConnection connection, CancellationToken cancellationToken = default)
        {
            events.Add($"{name}.commit");
            return Task.CompletedTask;
        }

        public Task AfterRollbackAsync(System.Data.Common.DbConnection connection, CancellationToken cancellationToken = default)
        {
            events.Add($"{name}.rollback");
            return Task.CompletedTask;
        }
    }

    private sealed class VetoInterceptor(List<string> events) : IUnitOfWorkInterceptor
    {
        public Task BeforeCommitAsync(
            System.Data.Common.DbConnection connection,
            System.Data.Common.DbTransaction transaction,
            CancellationToken cancellationToken = default)
        {
            events.Add("veto.before");
            throw new InvalidOperationException("commit vetoed");
        }
    }

    private sealed class FailingAfterCommitInterceptor(List<string> events) : IUnitOfWorkInterceptor
    {
        public Task AfterCommitAsync(System.Data.Common.DbConnection connection, CancellationToken cancellationToken = default)
        {
            events.Add("first.commit");
            throw new InvalidOperationException("after commit failed");
        }
    }

    private sealed class TestDatabase
    {
        private TestDatabase(string connectionString)
        {
            ConnectionString = connectionString;
            Factory = new DelegateDbConnectionFactory(DatabaseDialect.Sqlite, () => new SqliteConnection(connectionString));
        }

        public string ConnectionString { get; }
        public DelegateDbConnectionFactory Factory { get; }
        public List<string> Events { get; } = new();

        public static async Task<TestDatabase> CreateAsync()
        {
            var database = new TestDatabase($"Data Source=unit-of-work-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            await using var anchor = new SqliteConnection(database.ConnectionString);
            await anchor.OpenAsync();
            return database;
        }
    }
}
