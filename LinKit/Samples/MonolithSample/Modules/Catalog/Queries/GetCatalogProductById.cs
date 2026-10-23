using System;
using System.Threading;
using System.Threading.Tasks;
using LinKit.Core.Cqrs;
using LinKit.Core.Endpoints;
using LinKit.Data;
using MonolithSample.Modules.Catalog.Domain;

namespace MonolithSample.Modules.Catalog.Queries;

[HttpGet("api/catalog/db-products/{id}", Summary = "Get product by Id via LinKit.Data")]
public record GetCatalogProductByIdQuery([FromRoute] Guid id) : IQuery<ProductEntity>;

[CqrsHandler]
public class GetCatalogProductByIdHandler(IRepository<ProductEntity, Guid> repository)
    : IQueryHandler<GetCatalogProductByIdQuery, ProductEntity>
{
    public async Task<ProductEntity> HandleAsync(GetCatalogProductByIdQuery query, CancellationToken cancellationToken)
    {
        var product = await repository.GetByIdAsync(query.id, cancellationToken);
        return product!;
    }
}
