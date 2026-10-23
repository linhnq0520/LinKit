using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LinKit.Data;
using MonolithSample.Modules.Catalog.Domain;

namespace MonolithSample.Modules.Catalog.Migrations;

/// <summary>
/// Sample migration demonstrating compile-time discovered seed migration in LinKit.Data.
/// </summary>
[Migration("20261021_01", Description = "Seed initial sample products for catalog")]
public class SeedCatalogInitialProductsMigration : IMigration
{
    public async Task UpAsync(IMigrationContext context, CancellationToken ct = default)
    {
        // Type-safe check using LINQ (Cách 1)
        if (!await context.ExistsAsync<ProductEntity>(p => p.Sku == "SEED-001", ct))
        {
            var repo = context.GetRepository<ProductEntity, int>();
            await repo.InsertAsync(new ProductEntity
            {
                Name = "Master Seed Product",
                Price = 149.99m,
                Stock = 50,
                Sku = "SEED-001",
                IsFeatured = true,
                Status = ProductStatus.Active,
                ReleasedDate = DateOnly.FromDateTime(DateTime.UtcNow),
                DailyRestockTime = new TimeOnly(8, 0),
                Tags = new List<string> { "Seed", "Demo" },
                CreatedAt = DateTimeOffset.UtcNow
            }, ct);
        }
    }
}
