using LinKit.Core.Cqrs;
using LinKit.Core.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MicroserviceSample.ApiGateway.Features.Gateway;

public record GatewayCheckoutResponse(Guid TrackingId, string Status, string Message);

[ApiEndpoint(
    ApiMethod.Post,
    "api/gateway/checkout",
    Summary = "BFF Checkout: Nhận đơn hàng từ Mobile/Web và xác nhận thanh toán",
    Tag = "Gateway-BFF",
    SuccessStatusCode = StatusCodes.Status202Accepted,
    RateLimitPolicy = "GatewayRateLimit"
)]
public record GatewayCheckoutCommand(
    string CustomerId,
    string ProductCode,
    int Quantity
) : ICommand<GatewayCheckoutResponse>;

[CqrsHandler]
public class GatewayCheckoutCommandHandler(ILogger<GatewayCheckoutCommandHandler> logger)
    : ICommandHandler<GatewayCheckoutCommand, GatewayCheckoutResponse>
{
    public Task<GatewayCheckoutResponse> HandleAsync(
        GatewayCheckoutCommand request,
        CancellationToken cancellationToken = default
    )
    {
        var trackingId = Guid.NewGuid();
        logger.LogInformation(
            "[Gateway BFF] Tiếp nhận yêu cầu thanh toán cho khách {Customer}, sản phẩm {Product}, Số lượng: {Qty}. TrackingId: {TrackingId}",
            request.CustomerId,
            request.ProductCode,
            request.Quantity,
            trackingId
        );

        return Task.FromResult(new GatewayCheckoutResponse(
            TrackingId: trackingId,
            Status: "Accepted",
            Message: "Yêu cầu đặt hàng đã được tiếp nhận và chuyển tiếp xử lý tới OrderService."
        ));
    }
}
