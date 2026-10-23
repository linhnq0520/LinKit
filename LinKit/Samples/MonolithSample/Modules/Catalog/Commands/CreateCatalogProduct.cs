using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LinKit.Core.Cqrs;
using LinKit.Core.Endpoints;
using LinKit.Data;
using MonolithSample.Modules.Catalog.Domain;

namespace MonolithSample.Modules.Catalog.Commands;

[HttpPost("api/catalog/db-products", Summary = "Create a product via LinKit.Data")]
public record CreateCatalogProductCommand(
    string Name,
    decimal Price,
    int Stock,
    string? Sku = null,
    bool IsFeatured = false,
    ProductStatus Status = ProductStatus.Active,
    List<string>? Tags = null
) : ICommand<int>;

[CqrsHandler]
public class CreateCatalogProductHandler(IRepository<ProductEntity, int> repository)
    : ICommandHandler<CreateCatalogProductCommand, int>
{
    public async Task<int> HandleAsync(CreateCatalogProductCommand request, CancellationToken cancellationToken)
    {
        var product = new ProductEntity
        {
            Name = request.Name,
            Price = request.Price,
            Stock = request.Stock,
            Sku = request.Sku,
            IsFeatured = request.IsFeatured,
            Status = request.Status,
            ReleasedDate = DateOnly.FromDateTime(DateTime.UtcNow),
            DailyRestockTime = new TimeOnly(8, 30),
            Tags = request.Tags ?? new List<string> { "electronics", "new" },
            CreatedAt = DateTimeOffset.UtcNow
        };

        await repository.InsertAsync(product, cancellationToken);
        return product.Id;
    }
}

