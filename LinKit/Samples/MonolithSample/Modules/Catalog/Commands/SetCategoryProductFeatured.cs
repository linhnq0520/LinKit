using System.Threading;
using System.Threading.Tasks;
using LinKit.Core.Cqrs;
using LinKit.Core.Endpoints;
using LinKit.Data;
using MonolithSample.Modules.Catalog.Domain;

namespace MonolithSample.Modules.Catalog.Commands;

[HttpPut("api/catalog/db-products/category-featured", Summary = "Bulk update featured status for matching products")]
public sealed record SetCategoryProductFeaturedCommand(
    int CategoryId,
    int MinimumStock,
    bool IsFeatured) : ICommand<int>;

[CqrsHandler]
public sealed class SetCategoryProductFeaturedHandler(ProductEntityTypedRepository products)
    : ICommandHandler<SetCategoryProductFeaturedCommand, int>
{
    public Task<int> HandleAsync(
        SetCategoryProductFeaturedCommand request,
        CancellationToken cancellationToken)
    {
        var categoryId = request.CategoryId;
        var minimumStock = request.MinimumStock;
        return products.Query()
            .Where(product =>
                product.CategoryId.Equal(categoryId)
                & product.Stock >= minimumStock
                & product.Status == ProductStatus.Active)
            .ExecuteUpdateAsync(
                columns => [columns.IsFeatured.Set(request.IsFeatured)],
                ct: cancellationToken);
    }
}
