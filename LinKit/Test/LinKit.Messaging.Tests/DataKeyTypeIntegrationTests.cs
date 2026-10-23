using System.Data.Common;
using LinKit.Data;
using LinKit.Data.Synchronization;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;
using Xunit;

namespace LinKit.Messaging.Tests;

[Table("kt_int")]
public class KeyIntIdentity
{
    [PrimaryKey(AutoIncrement = true)]
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

[Table("kt_long")]
public class KeyLongIdentity
{
    [PrimaryKey(AutoIncrement = true)]
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

[Table("kt_guid")]
public class KeyGuid
{
    [PrimaryKey]
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

[Table("kt_string")]
public class KeyString
{
    [PrimaryKey]
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

[Table("kt_int_manual")]
public class KeyIntManual
{
    [PrimaryKey]
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

[Table("kt_long_manual")]
public class KeyLongManual
{
    [PrimaryKey]
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

public sealed class DataKeyTypeIntegrationTests
{
    private static readonly string[] Tables = ["kt_int", "kt_long", "kt_guid", "kt_string", "kt_int_manual", "kt_long_manual"];

    [Fact]
    public Task SqliteKeyTypes() =>
        RunAsync(DatabaseDialect.Sqlite, () => new SqliteConnection("Data Source=LinKitKeyTypes;Mode=Memory;Cache=Shared"), keepAlive: true);

    [ProviderFact("LINKIT_TEST_POSTGRES")]
    public Task PostgreSqlKeyTypes() =>
        RunAsync(DatabaseDialect.PostgreSql, () => new NpgsqlConnection(Environment.GetEnvironmentVariable("LINKIT_TEST_POSTGRES")));

    [ProviderFact("LINKIT_TEST_SQLSERVER")]
    public Task SqlServerKeyTypes() =>
        RunAsync(DatabaseDialect.SqlServer, () => new SqlConnection(Environment.GetEnvironmentVariable("LINKIT_TEST_SQLSERVER")));

    [ProviderFact("LINKIT_TEST_MYSQL")]
    public Task MySqlKeyTypes() =>
        RunAsync(DatabaseDialect.MySql, () => new MySqlConnection(Environment.GetEnvironmentVariable("LINKIT_TEST_MYSQL")));

    [ProviderFact("LINKIT_TEST_ORACLE")]
    public Task OracleKeyTypes() =>
        RunAsync(DatabaseDialect.Oracle, () => new OracleConnection(Environment.GetEnvironmentVariable("LINKIT_TEST_ORACLE")));

    private static async Task RunAsync(DatabaseDialect dialect, Func<DbConnection> create, bool keepAlive = false)
    {
        await using var anchor = keepAlive ? create() : null;
        if (anchor != null) await anchor.OpenAsync();

        await DropTablesAsync(dialect, create);

        var services = new ServiceCollection();
        services.AddLinKitData(options =>
        {
            options.UseConnection(dialect, create);
            options.EnsureDatabaseCreated = false;
            options.AutoSyncSchema = false;
            options.RunMigrations = false;
        });
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        await sp.GetRequiredService<ISchemaSynchronizer>().SynchronizeAsync();
        await sp.GetRequiredService<ISchemaSynchronizer>().SynchronizeAsync(); // idempotent

        // int identity: single + batch write-back
        var ints = sp.GetRequiredService<IRepository<KeyIntIdentity, int>>();
        var i1 = new KeyIntIdentity { Name = "a" };
        await ints.InsertAsync(i1);
        Assert.True(i1.Id > 0);
        var batch = new[] { new KeyIntIdentity { Name = "b" }, new KeyIntIdentity { Name = "c" } };
        await ints.InsertRangeAsync(batch);
        Assert.True(batch[0].Id > i1.Id && batch[1].Id > batch[0].Id);
        Assert.Equal("c", (await ints.GetByIdAsync(batch[1].Id))!.Name);
        i1.Name = "a2";
        await ints.UpdateAsync(i1);
        Assert.Equal("a2", (await ints.GetByIdAsync(i1.Id))!.Name);
        Assert.True(await ints.ExistsAsync(i1.Id));
        await ints.DeleteAsync(i1.Id);
        Assert.False(await ints.ExistsAsync(i1.Id));

        // long identity
        var longs = sp.GetRequiredService<IRepository<KeyLongIdentity, long>>();
        var l1 = new KeyLongIdentity { Name = "l" };
        await longs.InsertAsync(l1);
        Assert.True(l1.Id > 0);
        Assert.Equal("l", (await longs.GetByIdAsync(l1.Id))!.Name);

        // Guid assigned by the application
        var guids = sp.GetRequiredService<IRepository<KeyGuid, Guid>>();
        var gid = Guid.NewGuid();
        await guids.InsertAsync(new KeyGuid { Id = gid, Name = "g" });
        var g = await guids.GetByIdAsync(gid);
        Assert.NotNull(g);
        Assert.Equal(gid, g!.Id);
        g.Name = "g2";
        await guids.UpdateAsync(g);
        Assert.Equal("g2", (await guids.GetByIdAsync(gid))!.Name);
        await guids.DeleteAsync(gid);
        Assert.Null(await guids.GetByIdAsync(gid));

        // string natural key
        var strings = sp.GetRequiredService<IRepository<KeyString, string>>();
        await strings.InsertAsync(new KeyString { Code = "A-1", Name = "s" });
        var s = await strings.GetByIdAsync("A-1");
        Assert.NotNull(s);
        s!.Name = "s2";
        await strings.UpdateAsync(s);
        Assert.Equal("s2", (await strings.GetByIdAsync("A-1"))!.Name);
        await strings.DeleteAsync("A-1");
        Assert.False(await strings.ExistsAsync("A-1"));

        // assigned int and long (no identity), including values beyond int range
        var intManual = sp.GetRequiredService<IRepository<KeyIntManual, int>>();
        await intManual.InsertAsync(new KeyIntManual { Id = 42, Name = "m" });
        Assert.Equal("m", (await intManual.GetByIdAsync(42))!.Name);

        var longManual = sp.GetRequiredService<IRepository<KeyLongManual, long>>();
        await longManual.InsertAsync(new KeyLongManual { Id = 9_000_000_000L, Name = "big" });
        Assert.Equal("big", (await longManual.GetByIdAsync(9_000_000_000L))!.Name);

        await DropTablesAsync(dialect, create);
    }

    private static async Task DropTablesAsync(DatabaseDialect dialect, Func<DbConnection> create)
    {
        await using var conn = create();
        await conn.OpenAsync();
        foreach (var table in Tables)
        {
            var quoted = dialect switch
            {
                DatabaseDialect.SqlServer => $"[{table}]",
                DatabaseDialect.MySql => $"`{table}`",
                _ => $"\"{table}\""
            };
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"DROP TABLE {quoted}";
                await cmd.ExecuteNonQueryAsync();
            }
            catch (DbException)
            {
            }
        }
    }
}
