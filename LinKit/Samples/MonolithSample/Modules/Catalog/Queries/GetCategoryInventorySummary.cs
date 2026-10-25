using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LinKit.Core.Cqrs;
using LinKit.Core.Endpoints;
using LinKit.Data;
using MonolithSample.Modules.Catalog.Domain;
using MonolithSample.Modules.Catalog.Models;

namespace MonolithSample.Modules.Catalog.Queries;

[HttpGet("api/catalog/db-products/category-summary", Summary = "Summarize filtered product inventory by category")]
public sealed record GetCategoryInventorySummaryQuery(
    [FromQuery] decimal MinPrice = 0,
    [FromQuery] decimal MinCategoryTotal = 0,
    [FromQuery] string? CategoryName = null,
    [FromQuery] bool OnlyCategorized = false) : IQuery<List<CategoryInventorySummaryDto>>;

[CqrsHandler]
public sealed class GetCategoryInventorySummaryHandler(
    ProductEntityTypedRepository products,
    CategoryEntityTypedRepository categories)
    : IQueryHandler<GetCategoryInventorySummaryQuery, List<CategoryInventorySummaryDto>>
{
    public Task<List<CategoryInventorySummaryDto>> HandleAsync(
        GetCategoryInventorySummaryQuery request,
        CancellationToken cancellationToken)
    {
        var minimumPrice = request.MinPrice;
        var minimumTotal = request.MinCategoryTotal;
        var query = products.Query()
            .Where(product => product.Status == ProductStatus.Active)
            .WhereIf(minimumPrice > 0, product => product.Price >= minimumPrice);

        if (request.OnlyCategorized)
        {
            query = query.WhereExists(
                categories,
                (product, category) => product.CategoryId.Equal(category.Id));
        }

        var categoryName = request.CategoryName;
        return query
            .LeftJoin(categories, (product, category) => product.CategoryId.Equal(category.Id))
            .WhereIf(
                !string.IsNullOrWhiteSpace(categoryName),
                (_, category) => category.Name == categoryName)
            .GroupBy((product, category) => [product.CategoryId, category.Name])
            .Having((product, _) => Sql.Sum(product.Price).GreaterThanOrEqual(minimumTotal))
            .Select((product, category) => new Projection<CategoryInventorySummaryDto>(
                [
                    product.CategoryId,
                    category.Name,
                    Sql.Count(),
                    Sql.Sum(product.Price)
                ],
                reader => new CategoryInventorySummaryDto(
                    DbValues.Read<int?>(reader, 0),
                    DbValues.Read<string?>(reader, 1),
                    DbValues.Read<long>(reader, 2),
                    DbValues.Read<decimal?>(reader, 3))))
            .OrderByDescending((product, _) => Sql.Sum(product.Price))
            .ThenBy((product, _) => product.CategoryId)
            .ToListAsync(cancellationToken);
    }
}
