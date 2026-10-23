using LinKit.Core.Cqrs;
using Microsoft.Extensions.Logging;
using MonolithSample.Modules.Ordering.Events;

namespace MonolithSample.Modules.Notifications.Handlers;

[CqrsHandler]
public class SendEmailOnOrderPlaced(ILogger<SendEmailOnOrderPlaced> logger) : INotificationHandler<OrderPlacedEvent>
{
    public Task HandleAsync(OrderPlacedEvent notification, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("[Email Service] Đã gửi email xác nhận cho {Email} - Mã đơn: {OrderId}", notification.CustomerEmail, notification.OrderId);
        return Task.CompletedTask;
    }
}

[CqrsHandler]
public class AuditLogOnOrderPlaced(ILogger<AuditLogOnOrderPlaced> logger) : INotificationHandler<OrderPlacedEvent>
{
    public Task HandleAsync(OrderPlacedEvent notification, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("[Audit Log] Ghi nhận giao dịch đơn hàng {OrderId} trị giá {Total} VNĐ", notification.OrderId, notification.TotalAmount);
        return Task.CompletedTask;
    }
}
