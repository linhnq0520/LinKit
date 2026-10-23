using System.Text.Json;
using LinKit.Core.Abstractions;
using LinKit.Core.Cqrs;
using LinKit.Core.Endpoints;
using LinKit.Core.Messaging;
using LinKit.Outbox;
using LinKit.Grpc;
using MicroserviceSample.SharedContracts.Events;
using MicroserviceSample.SharedContracts.Models;
using Microsoft.Extensions.Logging;

namespace MicroserviceSample.OrderService.Features.Orders;

[HttpPost("api/orders", Summary = "Đặt hàng mới và kiểm tra tồn kho")]
public record CreateOrderCommand(
    string CustomerId,
    string ProductCode,
    int Quantity,
    decimal Price
) : ICommand<Guid>;

[CqrsHandler]
public class CreateOrderCommandHandler(
    IGrpcMediator grpcMediator,
    IMessagePublisher publisher,
    IOutboxStore outboxStore,
    ILogger<CreateOrderCommandHandler> logger
) : ICommandHandler<CreateOrderCommand, Guid>
{
    public async Task<Guid> HandleAsync(CreateOrderCommand request, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("[OrderService] Đang kiểm tra tồn kho qua gRPC cho sản phẩm {ProductCode}", request.ProductCode);

        // 1. Gọi sang InventoryService qua LinKit generated gRPC Mediator
        var stock = await grpcMediator.QueryAsync(new CheckStockQuery
        {
            ProductCode = request.ProductCode,
            Quantity = request.Quantity
        }, cancellationToken);

        if (!stock.IsAvailable)
        {
            throw new InvalidOperationException($"Sản phẩm {request.ProductCode} không đủ số lượng trong kho! (Tồn kho: {stock.RemainingStock})");
        }

        var orderId = Guid.NewGuid();
        var totalPrice = request.Quantity * request.Price;

        var orderSubmittedEvent = new OrderSubmittedEvent(
            orderId,
            request.CustomerId,
            request.ProductCode,
            request.Quantity,
            totalPrice
        );

        // 2. Lưu vào Transactional Outbox để đảm bảo tin nhắn không bao giờ bị thất lạc ngay cả khi Broker gặp sự cố
        logger.LogInformation("[OrderService] Lưu sự kiện vào Transactional Outbox cho đơn hàng {OrderId}", orderId);
        await outboxStore.SaveAsync(
            [OutboxMessage.Create(orderSubmittedEvent, JsonSerializer.Serialize(orderSubmittedEvent))],
            cancellationToken
        );

        // 3. Đồng thời xuất bản trực tiếp qua Message Broker (RabbitMQ/Kafka)
        await publisher.PublishAsync(orderSubmittedEvent, cancellationToken);

        return orderId;
    }
}
