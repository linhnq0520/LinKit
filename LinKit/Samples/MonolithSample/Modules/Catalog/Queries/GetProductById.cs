using LinKit.Caching;
using LinKit.Core.Cqrs;
using LinKit.Core.Endpoints;
using MonolithSample.Modules.Catalog.Models;

namespace MonolithSample.Modules.Catalog.Queries;

[HttpGet("api/catalog/products/{id}", Summary = "Lấy chi tiết sản phẩm theo ID (có hỗ trợ Cache)")]
public record GetProductByIdQuery([FromRoute] Guid Id) : IQuery<ProductDto>;

[CqrsHandler]
public class GetProductByIdQueryHandler(ICacheProvider cache) : IQueryHandler<GetProductByIdQuery, ProductDto>
{
    public async Task<ProductDto> HandleAsync(GetProductByIdQuery request, CancellationToken cancellationToken = default)
    {
        var cacheKey = $"catalog:product:{request.Id}";

        // Tận dụng Single-Flight Caching chống cache stampede
        var product = await cache.GetOrCreateAsync<ProductDto>(
            cacheKey,
            async token =>
            {
                // Giả lập đọc dữ liệu từ DB tốn 50ms
                await Task.Delay(50, token);
                return new ProductDto(request.Id, "Bàn phím cơ Custom", 1500000m, 20);
            },
            ttl: TimeSpan.FromMinutes(5),
            cancellationToken: cancellationToken
        );

        return product!;
    }
}
