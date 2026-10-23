using System.Data.Common;
using LinKit.Core.Messaging;
using LinKit.Data;
using LinKit.Outbox;
using LinKit.Outbox.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Time.Testing;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;
using Xunit;

namespace LinKit.Messaging.Tests;

internal sealed class ProviderFactAttribute : FactAttribute
{
    public ProviderFactAttribute(string connectionStringEnvironmentVariable)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(connectionStringEnvironmentVariable)))
            Skip = $"Set {connectionStringEnvironmentVariable} to run this database integration test.";
    }
}

public sealed class DatabaseProviderIntegrationTests
{
    [ProviderFact("LINKIT_TEST_POSTGRES")]
    public Task PostgreSqlOutboxAndInboxWorkAgainstDatabase() =>
        RunScenarioAsync(
            DatabaseDialect.PostgreSql,
            Environment.GetEnvironmentVariable("LINKIT_TEST_POSTGRES")!,
            connectionString => new NpgsqlConnection(connectionString));

    [ProviderFact("LINKIT_TEST_SQLSERVER")]
    public Task SqlServerOutboxAndInboxWorkAgainstDatabase() =>
        RunScenarioAsync(
            DatabaseDialect.SqlServer,
            Environment.GetEnvironmentVariable("LINKIT_TEST_SQLSERVER")!,
            connectionString => new SqlConnection(connectionString));

    [ProviderFact("LINKIT_TEST_MYSQL")]
    public Task MySqlOutboxAndInboxWorkAgainstDatabase() =>
        RunScenarioAsync(
            DatabaseDialect.MySql,
            Environment.GetEnvironmentVariable("LINKIT_TEST_MYSQL")!,
            connectionString => new MySqlConnection(connectionString));

    [ProviderFact("LINKIT_TEST_ORACLE")]
    public Task OracleOutboxAndInboxWorkAgainstDatabase() =>
        RunScenarioAsync(
            DatabaseDialect.Oracle,
            Environment.GetEnvironmentVariable("LINKIT_TEST_ORACLE")!,
            connectionString => new OracleConnection(connectionString));

    private static async Task RunScenarioAsync(
        DatabaseDialect dialect,
        string connectionString,
        Func<string, DbConnection> createConnection)
    {
        var time = new FakeTimeProvider();
        var factory = new DelegateDbConnectionFactory(dialect, () => createConnection(connectionString));
        await using var unitOfWork = new UnitOfWork(factory);
        var options = new OutboxOptions { MaxRetries = 3 };
        var outbox = new AdoNetOutboxStore(factory, unitOfWork, options, time);
        var inbox = new AdoNetInboxStore(factory, unitOfWork, time);

        await outbox.EnsureSchemaAsync();
        await inbox.EnsureSchemaAsync();
        await outbox.EnsureSchemaAsync();
        await inbox.EnsureSchemaAsync();

        var rolledBackMessage = CreateMessage();
        await unitOfWork.BeginTransactionAsync();
        await outbox.SaveMessageAsync(rolledBackMessage);
        await unitOfWork.RollbackAsync();
        Assert.DoesNotContain(await outbox.GetPendingAsync(), message => message.Id == rolledBackMessage.Id);

        var message = CreateMessage();
        await outbox.SaveMessageAsync(message);
        var stored = Assert.IsType<OutboxMessage>(Assert.Single(await outbox.GetPendingAsync()));
        Assert.Equal(message.Id, stored.Id);
        Assert.Equal("integration", stored.Headers!.CorrelationId);

        Assert.Equal(message.Id, Assert.Single(await outbox.ClaimPendingAsync(1, TimeSpan.FromMinutes(1))).Id);
        Assert.Empty(await outbox.ClaimPendingAsync(1, TimeSpan.FromMinutes(1)));

        await outbox.MarkFailedAsync(message.Id, "transient", time.GetUtcNow().UtcDateTime.AddSeconds(5));
        Assert.Empty(await outbox.GetPendingAsync());
        time.Advance(TimeSpan.FromSeconds(6));
        Assert.Single(await outbox.GetPendingAsync());
        await outbox.MarkProcessedAsync(message.Id);
        Assert.Empty(await outbox.GetPendingAsync());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => outbox.MarkProcessedAsync(Guid.NewGuid()));

        var inboxId = $"integration-{Guid.NewGuid():N}";
        await inbox.MarkProcessedAsync(inboxId, "provider-test");
        await inbox.MarkProcessedAsync(inboxId, "provider-test");
        Assert.True(await inbox.HasBeenProcessedAsync(inboxId, "provider-test"));
        Assert.False(await inbox.HasBeenProcessedAsync(inboxId, "another-consumer"));

        var expiringId = $"expiry-{Guid.NewGuid():N}";
        await inbox.MarkProcessedAsync(expiringId, "provider-test", TimeSpan.Zero);
        Assert.False(await inbox.HasBeenProcessedAsync(expiringId, "provider-test"));
    }

    private static OutboxMessage CreateMessage() => new()
    {
        MessageType = "ProviderIntegrationProbe",
        Payload = """{"ok":true}""",
        Headers = new MessageHeaders { [MessageHeaderNames.CorrelationId] = "integration" }
    };

}
