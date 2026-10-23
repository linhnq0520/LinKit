using LinKit.Core.Cqrs;
using LinKit.Core.Endpoints;
using MonolithSample.Modules.Catalog.Models;

namespace MonolithSample.Modules.Catalog.Queries;

[HttpGet("api/catalog/products/{id}", Summary = "Lấy chi tiết sản phẩm theo ID")]
public record GetProductByIdQuery([FromRoute] Guid Id) : IQuery<ProductDto>;

[CqrsHandler]
public class GetProductByIdQueryHandler : IQueryHandler<GetProductByIdQuery, ProductDto>
{
    public Task<ProductDto> HandleAsync(GetProductByIdQuery request, CancellationToken cancellationToken = default)
    {
        var product = new ProductDto(request.Id, "Bàn phím cơ Custom", 1500000m, 20);
        return Task.FromResult(product);
    }
}
