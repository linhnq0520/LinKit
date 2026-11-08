using LinKit.Data;
using LinKit.Data.Synchronization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LinKit.Messaging.Tests;

/// <summary>
/// Entity that relies on the property-level attribute spellings only: the tenant column is declared with
/// <see cref="TenantKeyAttribute"/> (and is deliberately not called <c>TenantId</c>), the concurrency token with
/// <see cref="ConcurrencyTokenAttribute"/>.
/// </summary>
[Table("alias_tenant_items")]
public class AliasTenantItem
{
    [PrimaryKey(AutoIncrement = true)]
    public long Id { get; set; }

    [TenantKey]
    public string Workspace { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    [ConcurrencyToken]
    public long Revision { get; set; }
}

/// <summary>
/// Covers the attribute spellings that are documented as synonyms of their canonical counterpart, so they cannot
/// silently become no-ops again.
/// </summary>
public sealed class DataAttributeAliasTests
{
    private static string ConnectionString() =>
        $"Data Source=alias-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

    private static ServiceProvider BuildProvider(string connectionString, Func<string?> tenant)
    {
        var services = new ServiceCollection();
        services.AddLinKitData(options =>
        {
            options.UseConnection(
                DatabaseDialect.Sqlite,
                () => new SqliteConnection(connectionString)
            );
            options.UseTenantId(_ => tenant());
            options.EnsureDatabaseCreated = false;
            options.AutoSyncSchema = false;
            options.RunMigrations = false;
        });

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task TenantKeyScopesRowsAndConcurrencyTokenGuardsUpdates()
    {
        var connectionString = ConnectionString();
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();

        string? current = "tenant-a";
        await using var provider = BuildProvider(connectionString, () => current);

        await using (var scope = provider.CreateAsyncScope())
        {
            await scope
                .ServiceProvider.GetRequiredService<ISchemaSynchronizer>()
                .SynchronizeAsync();
        }

        long mineId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var items = scope.ServiceProvider.GetRequiredService<AliasTenantItemTypedRepository>();

            // [TenantKey] supplies the tenant column, so the declared property is what gets scoped.
            var mine = new AliasTenantItem { Workspace = "tenant-a", Name = "mine" };
            await items.InsertAsync(mine);
            Assert.True(mine.Id > 0);
            Assert.Equal(1L, mine.Revision);
            Assert.Single(await items.ListAsync());

            // A stale [ConcurrencyToken] revision is rejected instead of overwriting the row.
            var stale = new AliasTenantItem
            {
                Id = mine.Id,
                Workspace = "tenant-a",
                Name = "stale",
                Revision = 99,
            };
            await Assert.ThrowsAsync<DataConcurrencyException>(() => items.UpdateAsync(stale));

            var fresh = await items.GetByIdAsync(mine.Id);
            Assert.NotNull(fresh);
            fresh.Name = "updated";
            await items.UpdateAsync(fresh);
            Assert.Equal(2L, fresh.Revision);

            mineId = mine.Id;
        }

        // A second tenant neither sees the row nor writes into the first tenant.
        current = "tenant-b";
        await using (var scope = provider.CreateAsyncScope())
        {
            var items = scope.ServiceProvider.GetRequiredService<AliasTenantItemTypedRepository>();

            Assert.Empty(await items.ListAsync());
            Assert.Null(await items.GetByIdAsync(mineId));

            var crossTenant = new AliasTenantItem { Workspace = "tenant-a", Name = "intruder" };
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                items.InsertAsync(crossTenant)
            );

            var theirs = new AliasTenantItem { Workspace = "tenant-b", Name = "theirs" };
            await items.InsertAsync(theirs);
            Assert.Single(await items.ListAsync());
        }
    }
}
