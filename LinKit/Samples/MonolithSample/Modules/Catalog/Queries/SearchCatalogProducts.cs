using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinKit.Core.Cqrs;
using LinKit.Core.Endpoints;
using LinKit.Data;
using MonolithSample.Modules.Catalog.Domain;

namespace MonolithSample.Modules.Catalog.Queries;

[HttpGet("api/catalog/db-products/paged", Summary = "Get paginated products via LinKit.Data Fluent Query")]
public record GetCatalogProductsPagedQuery(
    [FromQuery] int Page = 1,
    [FromQuery] int PageSize = 10,
    [FromQuery] decimal? MinPrice = null
) : IQuery<PagedResult<ProductEntity>>;

[CqrsHandler]
public class GetCatalogProductsPagedHandler(IRepository<ProductEntity, int> repository)
    : IQueryHandler<GetCatalogProductsPagedQuery, PagedResult<ProductEntity>>
{
    public async Task<PagedResult<ProductEntity>> HandleAsync(GetCatalogProductsPagedQuery query, CancellationToken cancellationToken)
    {
        IQueryable<ProductEntity> fluentQuery = repository.Table;

        if (query.MinPrice.HasValue)
        {
            fluentQuery = fluentQuery.Where(product => product.Price >= query.MinPrice.Value);
        }

        fluentQuery = fluentQuery.OrderBy(product => product.Price);

        return await fluentQuery.ToPagedListAsync(query.Page, query.PageSize, cancellationToken);
    }
}

[HttpGet("api/catalog/db-products/sql-raw", Summary = "Query products via Custom Raw SQL in LinKit.Data")]
public record QueryCatalogProductsRawSqlQuery([FromQuery] decimal MinPrice = 0) : IQuery<List<ProductEntity>>;

[CqrsHandler]
public class QueryCatalogProductsRawSqlHandler(IRepository<ProductEntity, int> repository)
    : IQueryHandler<QueryCatalogProductsRawSqlQuery, List<ProductEntity>>
{
    public async Task<List<ProductEntity>> HandleAsync(QueryCatalogProductsRawSqlQuery query, CancellationToken cancellationToken)
    {
        return await repository.QuerySqlAsync(
            "SELECT * FROM \"catalog_products\" WHERE \"Price\" >= @minPrice ORDER BY \"Price\" ASC",
            new Dictionary<string, object?> { ["minPrice"] = query.MinPrice },
            cancellationToken
        );
    }
}

[HttpGet("api/catalog/db-products/linq", Summary = "Query products via LINQ Query Comprehension syntax (from p in repo.AsQueryable())")]
public record QueryCatalogProductsLinqQuery(
    [FromQuery] decimal MinPrice = 0
) : IQuery<List<ProductEntity>>;

[CqrsHandler]
public class QueryCatalogProductsLinqHandler(IRepository<ProductEntity, int> repository)
    : IQueryHandler<QueryCatalogProductsLinqQuery, List<ProductEntity>>
{
    public async Task<List<ProductEntity>> HandleAsync(QueryCatalogProductsLinqQuery query, CancellationToken cancellationToken)
    {
        var min = query.MinPrice;

        var linqQuery = from p in repository.AsQueryable()
                        where p.Price >= min
                        orderby p.Price descending
                        select p;

        return await linqQuery.ToListAsync(cancellationToken);
    }
}

[HttpGet("api/catalog/db-products/typed-dto", Summary = "Query product summaries via Compile-time Typed Projection (AOT Mini-ORM)")]
public record QueryCatalogProductSummariesTypedQuery(
    [FromQuery] decimal MinPrice = 0
) : IQuery<List<MonolithSample.Modules.Catalog.Models.ProductSummaryDto>>;

[CqrsHandler]
public class QueryCatalogProductSummariesTypedHandler(ProductEntityTypedRepository repository)
    : IQueryHandler<QueryCatalogProductSummariesTypedQuery, List<MonolithSample.Modules.Catalog.Models.ProductSummaryDto>>
{
    public async Task<List<MonolithSample.Modules.Catalog.Models.ProductSummaryDto>> HandleAsync(
        QueryCatalogProductSummariesTypedQuery query,
        CancellationToken cancellationToken)
    {
        var min = query.MinPrice;

        return await repository.Query()
            .Where(p => p.Price >= min)
            .OrderByDescending(p => p.Price)
            .Select(MonolithSample.Modules.Catalog.Models.ProductSummaryDtoProjection.From)
            .ToListAsync(cancellationToken);
    }
}
