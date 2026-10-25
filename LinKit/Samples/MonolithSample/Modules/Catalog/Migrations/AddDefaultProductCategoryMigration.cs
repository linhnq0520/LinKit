using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LinKit.Data;
using LinKit.Data.Dialects;
using MonolithSample.Modules.Catalog.Domain;

namespace MonolithSample.Modules.Catalog.Migrations;

[Migration("20261024_01", Description = "Add the default category for catalog products")]
public sealed class AddDefaultProductCategoryMigration : IMigration
{
    public async Task UpAsync(IMigrationContext context, CancellationToken ct = default)
    {
        var categories = context.GetRepository<CategoryEntity, int>();
        var category = await categories.Table
            .Where(item => item.Name == "General")
            .FirstOrDefaultAsync(ct);

        if (category is null)
        {
            category = new CategoryEntity { Name = "General" };
            await categories.InsertAsync(category, ct);
        }

        var dialect = SqlDialectFactory.GetDialect(context.Dialect);
        var productsTable = dialect.QuoteIdentifier("catalog_products");
        var categoryIdColumn = dialect.QuoteIdentifier(nameof(ProductEntity.CategoryId));
        var parameter = $"{dialect.ParameterPrefix}categoryId";
        await context.ExecuteSqlAsync(
            $"UPDATE {productsTable} SET {categoryIdColumn} = {parameter} WHERE {categoryIdColumn} IS NULL",
            new Dictionary<string, object?> { ["categoryId"] = category.Id },
            ct);
    }

    public Task DownAsync(IMigrationContext context, CancellationToken ct = default) =>
        Task.CompletedTask;
}
