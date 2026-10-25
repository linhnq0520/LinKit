using System.Text.Json.Serialization;
using LinKit.Caching;
using LinKit.Core;
using LinKit.Core.Cqrs;
using LinKit.Core.Messaging;
using LinKit.Data;
using LinKit.Outbox;
using LinKit.Outbox.Data;
using LinKit.Resilience;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

LinKitMessageJson.AddContext(SmokeJsonContext.Default);

var services = new ServiceCollection();
services.AddLinKitCqrs();
services.AddLinKitCaching();
services.AddLinKitResilience();
await using var provider = services.BuildServiceProvider();

// 1. Generated mediator dispatch (no reflection)
var mediator = provider.GetRequiredService<IMediator>();
var greeting = await mediator.QueryAsync(new GreetQuery("AOT"));
Check(greeting == "Hello, AOT!", "mediator dispatch");

// 2. Resilience pipeline: retry then success
int calls = 0;
var value = await ResiliencePipeline.ExecuteAsync("smoke", ct =>
{
    if (++calls < 3) throw new InvalidOperationException("transient");
    return Task.FromResult(7);
}, new ResilienceOptions { MaxRetries = 3, RetryDelay = TimeSpan.FromMilliseconds(1) });
Check(value == 7 && calls == 3, "resilience retry");

// 3. Cache single-flight
var cache = provider.GetRequiredService<ICacheProvider>();
var cached = await cache.GetOrCreateAsync("k", _ => Task.FromResult(41 + 1));
Check(cached == 42, "cache");

// 4. Generated LinKit.Data repository, LINQ translation, and DbDataReader mapping
var connectionString = "Data Source=LinKitAotSmoke;Mode=Memory;Cache=Shared";
await using var databaseAnchor = new SqliteConnection(connectionString);
await databaseAnchor.OpenAsync();
await using (var command = databaseAnchor.CreateCommand())
{
    command.CommandText = """
        CREATE TABLE smoke_records ("Id" INTEGER PRIMARY KEY, "Name" TEXT NOT NULL, "Score" INTEGER NOT NULL, "DeletedAt" TEXT NULL);
        CREATE TABLE smoke_auto ("Id" INTEGER PRIMARY KEY AUTOINCREMENT, "Name" TEXT NOT NULL);
        CREATE TABLE smoke_guid ("Id" TEXT PRIMARY KEY, "Name" TEXT NOT NULL);
        CREATE TABLE smoke_code ("Code" TEXT PRIMARY KEY, "Name" TEXT NOT NULL);
        CREATE TABLE smoke_long ("Id" INTEGER PRIMARY KEY, "Name" TEXT NOT NULL);
        INSERT INTO smoke_records ("Id", "Name", "Score", "DeletedAt") VALUES
            (1, 'low', 5, NULL),
            (2, 'match', 12, NULL),
            (3, 'high', 20, NULL),
            (4, 'deleted', 30, '2025-01-01T00:00:00Z');
        """;
    await command.ExecuteNonQueryAsync();
}

services.AddLinKitData(options =>
{
    options.UseConnection(DatabaseDialect.Sqlite, () => new SqliteConnection(connectionString));
    options.EnsureDatabaseCreated = false;
    options.AutoSyncSchema = false;
    options.RunMigrations = false;
});
services.AddLinKitAdoNetOutbox();
await using var dataProvider = services.BuildServiceProvider();
using (var scope = dataProvider.CreateScope())
{
    var repository = scope.ServiceProvider.GetRequiredService<IRepository<SmokeRecord, int>>();
    var customRepository = scope.ServiceProvider.GetRequiredService<ISmokeRecordRepository>();
    using (var canceled = new CancellationTokenSource())
    {
        canceled.Cancel();
        await CheckCanceledAsync(customRepository.FindByIdAsync(1, canceled.Token));
    }

    var autoRepository = scope.ServiceProvider.GetRequiredService<IRepository<SmokeAuto, int>>();
    var auto1 = new SmokeAuto { Name = "a" };
    var auto2 = new SmokeAuto { Name = "b" };
    await autoRepository.InsertAsync(auto1);
    await autoRepository.InsertAsync(auto2);
    Check(auto1.Id == 1 && auto2.Id == 2, "data identity id written back on insert");

    var guidRepository = scope.ServiceProvider.GetRequiredService<IRepository<SmokeGuid, Guid>>();
    var guidKey = Guid.NewGuid();
    await guidRepository.InsertAsync(new SmokeGuid { Id = guidKey, Name = "g" });
    var guidLoaded = await guidRepository.GetByIdAsync(guidKey);
    Check(guidLoaded is { Name: "g" } && guidLoaded.Id == guidKey, "data Guid primary key round trip");
    Check(await guidRepository.ExistsAsync(guidKey), "data Guid primary key exists");

    var codeRepository = scope.ServiceProvider.GetRequiredService<IRepository<SmokeCode, string>>();
    await codeRepository.InsertAsync(new SmokeCode { Code = "A-1", Name = "c" });
    var codeLoaded = await codeRepository.GetByIdAsync("A-1");
    Check(codeLoaded is { Name: "c" }, "data string primary key round trip");
    codeLoaded!.Name = "c2";
    await codeRepository.UpdateAsync(codeLoaded);
    Check((await codeRepository.GetByIdAsync("A-1"))!.Name == "c2", "data string primary key update");
    await codeRepository.DeleteAsync("A-1");
    Check(await codeRepository.GetByIdAsync("A-1") == null, "data string primary key delete");

    var longRepository = scope.ServiceProvider.GetRequiredService<IRepository<SmokeLong, long>>();
    await longRepository.InsertAsync(new SmokeLong { Id = 9_000_000_000L, Name = "l" });
    Check((await longRepository.GetByIdAsync(9_000_000_000L))?.Name == "l", "data long assigned primary key round trip");

    var minimumScore = 10;
    var query = repository.AsQueryable();
    var records = await query
        .Where(record => record.Score >= minimumScore)
        .OrderByDescending(record => record.Score)
        .Select(record => record)
        .ToListAsync();
    Check(records.Count == 2, "data LINQ result count");
    Check(records[0] is { Id: 3, Name: "high", Score: 20 }, "data generated reader and ordering");
    Check(records[1] is { Id: 2, Name: "match", Score: 12 }, "data LINQ closure parameter");

    var fluentMinimumScore = 10;
    var fluentExpressionRecords = await repository.Table
        .Where(record => record.Score >= fluentMinimumScore)
        .Where(record => record.Name.Contains("match"))
        .OrderByDescending(record => record.Score)
        .ThenBy(record => record.Name)
        .ToListAsync();
    Check(
        fluentExpressionRecords is [{ Id: 2, Name: "match", Score: 12 }],
        "AOT fluent expression predicates and distinct parameters");

    var scalarLong = await repository.ExecuteScalarSqlAsync<long>(
        "SELECT COUNT(*) FROM smoke_records"
    );
    Check(scalarLong == 4L, "data scalar exact-type fast path");
    var scalarInt = await repository.ExecuteScalarSqlAsync<int>(
        "SELECT COUNT(*) FROM smoke_records"
    );
    Check(scalarInt == 4, "data scalar conversion fallback");

    var fluentRecords = await repository.Query()
        .Where("Score >= @minimum", new[] { (Name: "minimum", Value: (object?)10) })
        .ToListAsync();
    Check(fluentRecords.Count == 2, "soft-delete filter in fluent query");

    var matchingCount = query.Count(record => record.Score >= minimumScore);
    Check(matchingCount == 2, "data terminal predicate without reflection");

    var outbox = (AdoNetOutboxStore)scope.ServiceProvider.GetRequiredService<IOutboxStore>();
    var inbox = (AdoNetInboxStore)scope.ServiceProvider.GetRequiredService<IInboxStore>();
    await outbox.EnsureSchemaAsync();
    await inbox.EnsureSchemaAsync();
    var outboxMessage = OutboxMessage.Create(new SmokeMessage("aot", 1), """{"orderId":"aot","quantity":1}""");
    await outbox.SaveMessageAsync(outboxMessage);
    Check((await outbox.ClaimPendingAsync(10, TimeSpan.FromSeconds(10))).Count == 1, "durable outbox claim");
    await inbox.MarkProcessedAsync("aot-message", "smoke-consumer");
    Check(await inbox.HasBeenProcessedAsync("aot-message", "smoke-consumer"), "durable inbox idempotency");
}

// 5. Broker JSON contract using source-generated context
var bytes = LinKitMessageJson.SerializeToUtf8Bytes(new SmokeMessage("o-1", 3));
var back = LinKitMessageJson.Deserialize<SmokeMessage>(bytes);
Check(back is { OrderId: "o-1", Quantity: 3 }, "message json round trip");

Console.WriteLine("AOT smoke OK");
return 0;

static void Check(bool ok, string name)
{
    if (!ok)
    {
        Console.Error.WriteLine($"AOT smoke FAILED: {name}");
        Environment.Exit(1);
    }
}

static async Task CheckCanceledAsync(Task task)
{
    try
    {
        await task;
        Check(false, "custom repository cancellation");
    }
    catch (OperationCanceledException)
    {
        Check(true, "custom repository cancellation");
    }
}

public record GreetQuery(string Name) : IQuery<string>;

[CqrsHandler]
public class GreetQueryHandler : IQueryHandler<GreetQuery, string>
{
    public Task<string> HandleAsync(GreetQuery query, CancellationToken cancellationToken) =>
        Task.FromResult($"Hello, {query.Name}!");
}

[SoftDelete]
[Table("smoke_records")]
public class SmokeRecord
{
    [PrimaryKey]
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int Score { get; set; }

    public DateTime? DeletedAt { get; set; }
}

[Table("smoke_auto")]
public class SmokeAuto
{
    [PrimaryKey(AutoIncrement = true)]
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

[Table("smoke_guid")]
public class SmokeGuid
{
    [PrimaryKey]
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

[Table("smoke_code")]
public class SmokeCode
{
    [PrimaryKey]
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

[Table("smoke_long")]
public class SmokeLong
{
    [PrimaryKey]
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

[DataRepository]
public interface ISmokeRecordRepository : IRepository<SmokeRecord, int>
{
    [SqlQuery("SELECT Id, Name, Score, DeletedAt FROM smoke_records WHERE Id = @id")]
    Task<SmokeRecord?> FindByIdAsync(int id, CancellationToken cancellationToken = default);
}

public record SmokeMessage(string OrderId, int Quantity);

[JsonSerializable(typeof(SmokeMessage))]
public partial class SmokeJsonContext : JsonSerializerContext;
