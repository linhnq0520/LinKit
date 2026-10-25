using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LinKit.Core.Cqrs;
using LinKit.Core.Endpoints;
using LinKit.Data;
using MonolithSample.Modules.Catalog.Domain;

namespace MonolithSample.Modules.Catalog.Queries;

[HttpGet(
    "api/catalog/db-products/by-category",
    Summary = "Search products using compile-time typed category join (AOT Mini-ORM)"
)]
public sealed record SearchProductsByCategoryQuery(
    [FromQuery] string Category,
    [FromQuery] decimal MinPrice = 0,
    [FromQuery] int MinStock = 0
) : IQuery<List<ProductEntity>>;

[CqrsHandler]
public sealed class SearchProductsByCategoryHandler(CatalogDataContext data)
    : IQueryHandler<SearchProductsByCategoryQuery, List<ProductEntity>>
{
    public Task<List<ProductEntity>> HandleAsync(
        SearchProductsByCategoryQuery query,
        CancellationToken cancellationToken)
    {
        var categoryName = query.Category;
        var minPrice = query.MinPrice;
        var minStock = query.MinStock;

        // Compile-time strongly-typed SQL DSL with 0 reflection and 0 runtime Expression tree parsing
        var typedQuery = data.ProductEntity.Query()
            .InnerJoin(data.CategoryEntity, (p, c) => p.CategoryId.Equal(c.Id))
            .Where((p, c) => c.Name == categoryName
                & p.Price >= minPrice
                & (p.Stock >= minStock | p.IsFeatured == true))
            .OrderByDescending((p, c) => p.Price)
            .ThenBy((p, c) => p.Id);

        return typedQuery.ToListAsync(cancellationToken);
    }
}
