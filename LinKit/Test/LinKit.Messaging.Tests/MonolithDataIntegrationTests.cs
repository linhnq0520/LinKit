using LinKit.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using MonolithSample.Modules.Catalog.Commands;
using MonolithSample.Modules.Catalog.Domain;
using MonolithSample.Modules.Catalog.Queries;
using Xunit;

namespace LinKit.Messaging.Tests;

public sealed class MonolithDataIntegrationTests
{
    [Fact]
    public async Task MonolithCatalogQueriesAndCrudExecuteAgainstSqlite()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"linkit-monolith-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString();

        try
        {
            var dialect = new SqlDialect(DatabaseKind.Sqlite);
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                foreach (var model in new[] { CategoryEntityMap.TableModel, ProductEntityMap.TableModel })
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = dialect.CreateTable(model);
                    await command.ExecuteNonQueryAsync();
                }
            }

            var services = new ServiceCollection();
            MonolithSample.Generated.LinKitDataGeneratedServiceExtensions.AddLinKitData(services, options =>
            {
                options.UseConnection(DatabaseDialect.Sqlite, () => new SqliteConnection(connectionString));
                options.EnsureDatabaseCreated = false;
                options.AutoSyncSchema = false;
                options.RunMigrations = false;
            });
            services.AddTransient<GetCategoryInventorySummaryHandler>();
            services.AddTransient<SetCategoryProductFeaturedHandler>();
            services.AddTransient<GetCatalogProductByIdHandler>();
            services.AddTransient<GetCatalogProductsPagedHandler>();
            services.AddTransient<QueryCatalogProductsLinqHandler>();
            services.AddTransient<QueryCatalogProductsRawSqlHandler>();

            await using (var provider = services.BuildServiceProvider())
            await using (var scope = provider.CreateAsyncScope())
            {
                var session = scope.ServiceProvider.GetRequiredService<DataSession>();
                var categories = scope.ServiceProvider.GetRequiredService<CategoryEntityTypedRepository>();
                var products = scope.ServiceProvider.GetRequiredService<ProductEntityTypedRepository>();
                var linqProducts = scope.ServiceProvider.GetRequiredService<IRepository<ProductEntity, int>>().Table;
                var books = new CategoryEntity { Name = "Books" };
                var games = new CategoryEntity { Name = "Games" };
                await categories.InsertRangeAsync([books, games]);

                var first = NewProduct("Typed DSL", 12m, 15, books.Id);
                var second = NewProduct("Query Planner", 20m, 4, books.Id);
                var third = NewProduct("Uncategorized", 5m, 20, null);
                var fourth = NewProduct("Board Game", 30m, 8, games.Id);
                await products.InsertRangeAsync([first, second, third, fourth]);
                Assert.Equal(4, await products.CountAsync());
                Assert.Equal(ProductStatus.Active, (await products.GetByIdAsync(first.Id))!.Status);
                Assert.Equal(
                    3,
                    await products.Query()
                        .Where(product => product.Status == ProductStatus.Active & product.Price >= 10m)
                        .CountAsync());
                var summaries = await scope.ServiceProvider.GetRequiredService<GetCategoryInventorySummaryHandler>()
                    .HandleAsync(
                        new GetCategoryInventorySummaryQuery(
                            MinPrice: 10m,
                            MinCategoryTotal: 10m,
                            OnlyCategorized: true),
                        CancellationToken.None);
                Assert.Equal(2, summaries.Count);
                Assert.Equal("Books", summaries.Single(summary => summary.CategoryName == "Books").CategoryName);
                Assert.Equal(2, summaries[0].ProductCount);
                Assert.Equal(32m, summaries.Single(summary => summary.CategoryName == "Books").TotalPrice);
                Assert.Equal(1, summaries.Single(summary => summary.CategoryName == "Games").ProductCount);
                Assert.Equal(30m, summaries.Single(summary => summary.CategoryName == "Games").TotalPrice);

                var leftJoinSummaries = await scope.ServiceProvider.GetRequiredService<GetCategoryInventorySummaryHandler>()
                    .HandleAsync(
                        new GetCategoryInventorySummaryQuery(MinCategoryTotal: 0m),
                        CancellationToken.None);
                Assert.Equal(3, leftJoinSummaries.Count);
                Assert.Equal(5m, leftJoinSummaries.Single(summary => summary.CategoryId is null).TotalPrice);

                var updated = await scope.ServiceProvider.GetRequiredService<SetCategoryProductFeaturedHandler>()
                    .HandleAsync(
                        new SetCategoryProductFeaturedCommand(books.Id, MinimumStock: 10, IsFeatured: true),
                        CancellationToken.None);
                Assert.Equal(1, updated);
                Assert.True((await products.GetByIdAsync(first.Id))!.IsFeatured);
                Assert.False((await products.GetByIdAsync(second.Id))!.IsFeatured);

                first.Name = "Typed DSL updated";
                await products.UpdateAsync(first);
                Assert.Equal("Typed DSL updated", (await products.GetByIdAsync(first.Id))!.Name);

                var byId = await scope.ServiceProvider.GetRequiredService<GetCatalogProductByIdHandler>()
                    .HandleAsync(new GetCatalogProductByIdQuery(first.Id), CancellationToken.None);
                Assert.Equal(first.Id, byId.Id);

                var paged = await scope.ServiceProvider.GetRequiredService<GetCatalogProductsPagedHandler>()
                    .HandleAsync(
                        new GetCatalogProductsPagedQuery(Page: 1, PageSize: 10, MinPrice: 10m),
                        CancellationToken.None);
                Assert.Equal(3, paged.TotalCount);

                var linqResults = await scope.ServiceProvider.GetRequiredService<QueryCatalogProductsLinqHandler>()
                    .HandleAsync(new QueryCatalogProductsLinqQuery(10m), CancellationToken.None);
                Assert.Equal(3, linqResults.Count);

                var orderedProducts = linqProducts
                    .Where(product => product.Price >= 10m)
                    .OrderBy(product => product.Price);
                Assert.Equal(2, await orderedProducts.Skip(1).Take(2).CountAsync());
                Assert.Equal(1, await orderedProducts.Take(2).Skip(1).Take(5).CountAsync());
                Assert.True(await orderedProducts.Skip(2).Take(1).AnyAsync());
                Assert.False(await orderedProducts.Skip(3).AnyAsync());
                Assert.Equal(0, await orderedProducts.Take(0).CountAsync());
                Assert.False(await orderedProducts.Take(0).AnyAsync());
                Assert.Empty(await orderedProducts.Take(0).ToListAsync());
                Assert.Empty(await products.Query().Take(0).ToListAsync());
                Assert.Equal(
                    first.Id,
                    linqProducts.Single(product => product.Id == first.Id).Id);
                Assert.Null(await linqProducts
                    .Where(product => product.Name == "missing")
                    .FirstOrDefaultAsync());
                Assert.Throws<InvalidOperationException>(() =>
                {
                    _ = linqProducts.Where(product => product.Name == "missing").First();
                });
                Assert.Throws<InvalidOperationException>(() =>
                {
                    _ = orderedProducts.Single();
                });
                Assert.Null(linqProducts
                    .Where(product => product.Name == "missing")
                    .SingleOrDefault());

                var rawSqlResults = await scope.ServiceProvider.GetRequiredService<QueryCatalogProductsRawSqlHandler>()
                    .HandleAsync(new QueryCatalogProductsRawSqlQuery(10m), CancellationToken.None);
                Assert.Equal(3, rawSqlResults.Count);

                Assert.Equal(
                    1,
                    await products.Query()
                        .Where(product => product.CategoryId.IsNull())
                        .ExecuteDeleteAsync());
                Assert.Null(await products.GetByIdAsync(third.Id));

                await using (var transaction = await session.BeginTransactionAsync())
                {
                    var rolledBack = NewProduct("Rollback", 99m, 1, books.Id);
                    await products.InsertAsync(rolledBack);
                }
                Assert.Equal(0, await products.Query().Where(product => product.Name == "Rollback").CountAsync());

                await products.DeleteRangeAsync([first.Id, second.Id, fourth.Id]);
                Assert.Equal(0, await products.CountAsync());
            }
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    private static ProductEntity NewProduct(string name, decimal price, int stock, int? categoryId) => new()
    {
        Name = name,
        Price = price,
        Stock = stock,
        CategoryId = categoryId,
        Status = ProductStatus.Active,
        Tags = ["integration", name],
        CreatedAt = DateTimeOffset.UtcNow
    };
}
