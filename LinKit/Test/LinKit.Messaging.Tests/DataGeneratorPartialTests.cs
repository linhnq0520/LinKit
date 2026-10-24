using Xunit;
using LinKit.Data;
using Microsoft.Extensions.DependencyInjection;

namespace LinKit.Messaging.Tests;

public sealed class DataGeneratorPartialTests
{
    [Fact]
    public async Task GeneratedRepositoryContractAndDiCanBeExtendedWithPartialDeclarations()
    {
        Assert.Equal("extended", KeyIntIdentityRepository.CustomMarker);
        Assert.True(typeof(IKeyIntIdentityRepository).IsAssignableFrom(typeof(KeyIntIdentityRepository)));

        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLinKitData(options =>
        {
            options.UseConnection(
                DatabaseDialect.Sqlite,
                () => new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:"));
            options.EnsureDatabaseCreated = false;
            options.AutoSyncSchema = false;
            options.RunMigrations = false;
        });
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IKeyIntIdentityRepository>();

        Assert.Equal(42, await repository.CustomOperationAsync());
    }
}

public partial interface IKeyIntIdentityRepository
{
}
