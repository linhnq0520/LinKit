using LinKit.Core.Cqrs;
using LinKit.Core.Endpoints;
using LinKit.Grpc;
using MicroserviceSample.SharedContracts.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MicroserviceSample.ApiGateway.Features.Gateway;

public record ProductAggregatedResponse(
    string ProductCode,
    string ProductName,
    decimal Price,
    bool IsInStock,
    int AvailableStock,
    string StockStatusMessage
);

[ApiEndpoint(
    ApiMethod.Get,
    "api/gateway/products/{productCode}",
    Summary = "BFF Aggregator: Tổng hợp chi tiết sản phẩm và trạng thái tồn kho qua gRPC",
    Tag = "Gateway-BFF",
    ProducesStatusCodes = new[] { StatusCodes.Status200OK, StatusCodes.Status404NotFound }
)]
public record GetProductDetailsQuery([FromRoute] string ProductCode) : IQuery<ProductAggregatedResponse>;

[CqrsHandler]
public class GetProductDetailsQueryHandler(
    IGrpcMediator grpcMediator,
    ILogger<GetProductDetailsQueryHandler> logger
) : IQueryHandler<GetProductDetailsQuery, ProductAggregatedResponse>
{
    public async Task<ProductAggregatedResponse> HandleAsync(
        GetProductDetailsQuery request,
        CancellationToken cancellationToken = default
    )
    {
        logger.LogInformation("[Gateway BFF] Đang tổng hợp dữ liệu sản phẩm {ProductCode} từ các microservices...", request.ProductCode);

        // 1. Gọi sang InventoryService qua gRPC Client Mediator do LinKit sinh ra (kèm Interceptor CorrelationId)
        var stockResponse = await grpcMediator.QueryAsync(new CheckStockQuery
        {
            ProductCode = request.ProductCode,
            Quantity = 1
        }, cancellationToken);

        // 2. Giả lập lấy thông tin catalog/giá
        var price = 1200000m;
        var name = $"Sản phẩm cao cấp ({request.ProductCode})";

        // 3. Tổng hợp thành DTO duy nhất trả về cho Client
        return new ProductAggregatedResponse(
            ProductCode: request.ProductCode,
            ProductName: name,
            Price: price,
            IsInStock: stockResponse.IsAvailable,
            AvailableStock: stockResponse.RemainingStock,
            StockStatusMessage: stockResponse.Message
        );
    }
}
