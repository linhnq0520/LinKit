using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LinKit.Core.Cqrs;
using LinKit.Core.Endpoints;
using LinKit.Data;
using LinKit.Data.Dialects;
using MonolithSample.Modules.Catalog.Domain;

namespace MonolithSample.Modules.Catalog.Queries;

[HttpGet(
    "api/catalog/db-products/by-category",
    Summary = "Search products using a parameterized category join"
)]
public sealed record SearchProductsByCategoryQuery(
    [FromQuery] string Category,
    [FromQuery] decimal MinPrice = 0,
    [FromQuery] int MinStock = 0
) : IQuery<List<ProductEntity>>;

[CqrsHandler]
public sealed class SearchProductsByCategoryHandler(
    IRepository<ProductEntity, Guid> repository,
    IDbConnectionFactory connectionFactory)
    : IQueryHandler<SearchProductsByCategoryQuery, List<ProductEntity>>
{
    public Task<List<ProductEntity>> HandleAsync(
        SearchProductsByCategoryQuery query,
        CancellationToken cancellationToken)
    {
        var dialect = SqlDialectFactory.GetDialect(connectionFactory.Dialect);
        var products = dialect.QuoteIdentifier("catalog_products");
        var categories = dialect.QuoteIdentifier("catalog_categories");
        var productAlias = "p";
        var categoryAlias = "c";
        var categoryId = dialect.QuoteIdentifier(nameof(ProductEntity.CategoryId));
        var id = dialect.QuoteIdentifier(nameof(CategoryEntity.Id));
        var name = dialect.QuoteIdentifier(nameof(CategoryEntity.Name));
        var price = dialect.QuoteIdentifier(nameof(ProductEntity.Price));
        var stock = dialect.QuoteIdentifier(nameof(ProductEntity.Stock));
        var featured = dialect.QuoteIdentifier(nameof(ProductEntity.IsFeatured));
        var marker = dialect.ParameterPrefix;

        var sql = $"""
            SELECT {productAlias}.*
            FROM {products} AS {productAlias}
            INNER JOIN {categories} AS {categoryAlias}
                ON {productAlias}.{categoryId} = {categoryAlias}.{id}
            WHERE {categoryAlias}.{name} = {marker}category
                AND {productAlias}.{price} >= {marker}minPrice
                AND ({productAlias}.{stock} >= {marker}minStock
                    OR {productAlias}.{featured} = {marker}isFeatured)
            ORDER BY {productAlias}.{price} DESC, {productAlias}.{id} ASC
            """;

        return repository.QuerySqlAsync(
            sql,
            new Dictionary<string, object?>
            {
                ["category"] = query.Category,
                ["minPrice"] = query.MinPrice,
                ["minStock"] = query.MinStock,
                ["isFeatured"] = true
            },
            cancellationToken);
    }
}
