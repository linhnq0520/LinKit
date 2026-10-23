using LinKit.Core.Cqrs;
using LinKit.Core.Endpoints;
using Microsoft.AspNetCore.Http;
using MonolithSample.Modules.Catalog.Exceptions;
using MonolithSample.Modules.Catalog.Models;
using MonolithSample.Modules.Catalog.Services;

namespace MonolithSample.Modules.Catalog.Commands;

[ApiEndpoint(
    ApiMethod.Post,
    "api/catalog/products",
    Summary = "Tạo sản phẩm mới với giá chiết khấu tự động",
    Tag = "Catalog",
    SuccessStatusCode = StatusCodes.Status201Created,
    ProducesStatusCodes = new[] { StatusCodes.Status400BadRequest, StatusCodes.Status500InternalServerError },
    RateLimitPolicy = "FixedWindow"
)]
public record CreateProductCommand(
    string Name,
    decimal OriginalPrice,
    decimal DiscountPercentage,
    int InitialStock
) : ICommand<Guid>;

[CqrsHandler]
public class CreateProductCommandHandler(IPricingService pricingService)
    : ICommandHandler<CreateProductCommand, Guid>
{
    public Task<Guid> HandleAsync(CreateProductCommand request, CancellationToken cancellationToken = default)
    {
        if (request.InitialStock < 0)
        {
            throw new ProductOutOfStockException("Số lượng tồn kho ban đầu không thể âm!");
        }

        // Tận dụng IPricingService được LinKit tự động đăng ký qua [RegisterService]
        var finalPrice = pricingService.CalculateDiscountedPrice(request.OriginalPrice, request.DiscountPercentage);

        var productId = Guid.NewGuid();
        return Task.FromResult(productId);
    }
}
