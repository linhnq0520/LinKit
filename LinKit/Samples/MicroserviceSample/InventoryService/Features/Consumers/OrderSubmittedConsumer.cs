using LinKit.Core.Cqrs;
using LinKit.Outbox;
using MicroserviceSample.SharedContracts.Events;
using Microsoft.Extensions.Logging;

namespace MicroserviceSample.InventoryService.Features.Consumers;

[CqrsHandler]
public class OrderSubmittedConsumerHandler(
    IInboxStore inboxStore,
    ILogger<OrderSubmittedConsumerHandler> logger
) : ICommandHandler<OrderSubmittedEvent>
{
    public async Task<Unit> HandleAsync(OrderSubmittedEvent request, CancellationToken cancellationToken = default)
    {
        string messageId = request.OrderId.ToString();
        string consumerName = nameof(OrderSubmittedConsumerHandler);

        // 1. Kiểm tra Idempotency qua Inbox Store: Ngăn chặn xử lý lặp lại tin nhắn
        if (await inboxStore.HasBeenProcessedAsync(messageId, consumerName, cancellationToken))
        {
            logger.LogWarning("[InventoryService Consumer] Bỏ qua tin nhắn trùng lặp cho đơn hàng {OrderId}", request.OrderId);
            return Unit.Value;
        }

        logger.LogInformation(
            "[InventoryService Consumer] Đang xử lý OrderSubmittedEvent: Đơn: {OrderId}, SP: {ProductCode}, SL: {Quantity}, Tiền: {Total} VNĐ",
            request.OrderId,
            request.ProductCode,
            request.Quantity,
            request.TotalPrice
        );

        // 2. Giả lập trừ kho trong database thành công...

        // 3. Đánh dấu đã xử lý vào Inbox Store (TTL 24 giờ)
        await inboxStore.MarkProcessedAsync(messageId, consumerName, TimeSpan.FromHours(24), cancellationToken);

        return Unit.Value;
    }
}
