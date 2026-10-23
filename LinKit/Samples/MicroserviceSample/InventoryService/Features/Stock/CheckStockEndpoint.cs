using LinKit.Core.Cqrs;
using LinKit.Grpc;
using MicroserviceSample.SharedContracts.Grpc;

namespace MicroserviceSample.InventoryService.Features.Stock;

[GrpcEndpoint(typeof(MicroserviceSample.SharedContracts.Grpc.InventoryService.InventoryServiceBase), "CheckStock")]
public record CheckStockServerQuery : IQuery<CheckStockResponse>
{
    public string ProductCode { get; set; } = string.Empty;
    public int Quantity { get; set; }
}

[CqrsHandler]
public class CheckStockServerQueryHandler : IQueryHandler<CheckStockServerQuery, CheckStockResponse>
{
    public Task<CheckStockResponse> HandleAsync(CheckStockServerQuery request, CancellationToken cancellationToken = default)
    {
        bool isAvailable = request.Quantity <= 50;
        int remaining = isAvailable ? 50 - request.Quantity : 50;

        return Task.FromResult(new CheckStockResponse
        {
            IsAvailable = isAvailable,
            RemainingStock = remaining,
            Message = isAvailable ? "Sản phẩm đủ hàng" : "Kho không đủ số lượng yêu cầu"
        });
    }
}
