using LinKit.Core.Messaging;
using LinKit.Data;
using LinKit.Outbox;
using LinKit.Outbox.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace LinKit.Messaging.Tests;

public sealed class AdoNetOutboxStoreTests
{
    [Fact]
    public async Task OutboxPersistsAndClaimsMessagesWithLeaseAndBackoff()
    {
        var connectionString = $"Data Source=outbox-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        var time = new FakeTimeProvider();
        var factory = new DelegateDbConnectionFactory(DatabaseDialect.Sqlite, () => new SqliteConnection(connectionString));
        await using var unitOfWork = new UnitOfWork(factory);
        var options = new OutboxOptions { MaxRetries = 3, BaseRetryDelay = TimeSpan.FromSeconds(5) };
        var outbox = new AdoNetOutboxStore(factory, unitOfWork, options, time);
        var inbox = new AdoNetInboxStore(factory, unitOfWork, time);
        await outbox.EnsureSchemaAsync();
        await inbox.EnsureSchemaAsync();

        await using (var transaction = await unitOfWork.BeginTransactionAsync())
        {
            await using var businessCommand = transaction.Connection!.CreateCommand();
            businessCommand.Transaction = transaction;
            businessCommand.CommandText = "CREATE TABLE business_data (id INTEGER PRIMARY KEY)";
            await businessCommand.ExecuteNonQueryAsync();
            await outbox.SaveMessageAsync(new OutboxMessage
            {
                MessageType = "Created",
                Payload = """{"id":1}""",
                Headers = new MessageHeaders { [MessageHeaderNames.CorrelationId] = "c-1", ["attempt"] = 2 }
            });
            await unitOfWork.RollbackAsync();
        }

        Assert.Empty(await outbox.GetPendingAsync());

        var message = new OutboxMessage
        {
            MessageType = "Created",
            Payload = """{"id":2}""",
            Headers = new MessageHeaders { [MessageHeaderNames.CorrelationId] = "c-2", ["attempt"] = 3 }
        };
        await unitOfWork.BeginTransactionAsync();
        await outbox.SaveMessageAsync(message);
        await unitOfWork.CommitAsync();

        var pending = await outbox.GetPendingAsync();
        var stored = Assert.IsType<OutboxMessage>(Assert.Single(pending));
        Assert.Equal(message.Id, stored.Id);
        Assert.Equal("c-2", stored.Headers!.CorrelationId);
        Assert.Equal(3L, stored.Headers["attempt"]);

        var firstClaim = await outbox.ClaimPendingAsync(10, TimeSpan.FromSeconds(10));
        Assert.Single(firstClaim);
        Assert.Empty(await outbox.ClaimPendingAsync(10, TimeSpan.FromSeconds(10)));

        await outbox.MarkFailedAsync(message.Id, "temporary", time.GetUtcNow().UtcDateTime + options.GetRetryDelay(1));
        Assert.Empty(await outbox.ClaimPendingAsync(10, TimeSpan.FromSeconds(10)));
        time.Advance(TimeSpan.FromSeconds(6));
        Assert.Single(await outbox.ClaimPendingAsync(10, TimeSpan.FromSeconds(10)));
        await outbox.MarkProcessedAsync(message.Id);
        Assert.Empty(await outbox.GetPendingAsync());
    }

    [Fact]
    public async Task InboxIsIdempotentByMessageAndConsumerAndHonorsExpiry()
    {
        var connectionString = $"Data Source=inbox-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        var time = new FakeTimeProvider();
        var factory = new DelegateDbConnectionFactory(DatabaseDialect.Sqlite, () => new SqliteConnection(connectionString));
        await using var unitOfWork = new UnitOfWork(factory);
        var inbox = new AdoNetInboxStore(factory, unitOfWork, time);
        await inbox.EnsureSchemaAsync();

        await inbox.MarkProcessedAsync("message-1", "consumer-a", TimeSpan.FromSeconds(5));
        await inbox.MarkProcessedAsync("message-1", "consumer-a", TimeSpan.FromSeconds(5));

        Assert.True(await inbox.HasBeenProcessedAsync("message-1", "consumer-a"));
        Assert.False(await inbox.HasBeenProcessedAsync("message-1", "consumer-b"));

        time.Advance(TimeSpan.FromSeconds(6));
        Assert.False(await inbox.HasBeenProcessedAsync("message-1", "consumer-a"));

        await inbox.MarkProcessedAsync("message-1", "consumer-a");
        Assert.True(await inbox.HasBeenProcessedAsync("message-1", "consumer-a"));
    }
}
