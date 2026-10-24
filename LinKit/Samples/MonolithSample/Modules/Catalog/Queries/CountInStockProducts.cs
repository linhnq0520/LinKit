using LinKit.Core.Cqrs;
using LinKit.Core.Endpoints;
using MonolithSample;

namespace MonolithSample.Modules.Catalog.Queries;

[HttpGet(
    "api/catalog/db-products/in-stock/count",
    Summary = "Count in-stock products using the extended partial repository"
)]
public sealed record CountInStockProductsQuery : IQuery<long>
{
}

[CqrsHandler]
public sealed class CountInStockProductsHandler(IProductEntityRepository repository)
    : IQueryHandler<CountInStockProductsQuery, long>
{
    public Task<long> HandleAsync(
        CountInStockProductsQuery query,
        CancellationToken cancellationToken) =>
        repository.CountInStockAsync(cancellationToken);
}
