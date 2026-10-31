using LinKit.Core.Cqrs;
using LinKit.Core.Endpoints;
using LinKit.Data;
using LinKit.Outbox;

namespace MonolithSample.Modules.Ordering.Commands;

/// <summary>
/// Demo use-case Transactional Outbox:
/// Ghi nhận đơn hàng vào Database và lưu Event vào Outbox table trong cùng 1 Transaction duy nhất!
/// Đảm bảo không bao giờ mất tin trong kiến trúc Event-Driven.
/// </summary>
[HttpPost("api/orders/place-with-outbox", Summary = "Tạo đơn hàng kèm Transactional Outbox")]
public record PlaceOrderWithOutboxCommand(string CustomerEmail, decimal Amount) : ICommand<Guid>;

[CqrsHandler]
public class PlaceOrderWithOutboxCommandHandler(DataSession session, IOutboxStore outboxStore) 
    : ICommandHandler<PlaceOrderWithOutboxCommand, Guid>
{
    public async Task<Guid> HandleAsync(PlaceOrderWithOutboxCommand request, CancellationToken ct = default)
    {
        var orderId = Guid.NewGuid();

        // Bắt đầu Transaction nguyên tử (ACID)
        await using var tx = await session.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        try
        {
            // 1. Ghi nhận nghiệp vụ đơn hàng (Insert DB)
            // session.Order...

            // 2. Ghi nhận sự kiện Outbox (Insert vào bảng __linkit_outbox)
            var outboxMessage = new OutboxMessage
            {
                Id = Guid.NewGuid(),
                MessageType = "OrderPlacedIntegrationEvent",
                Payload = $"{{\"orderId\":\"{orderId}\",\"amount\":{request.Amount},\"email\":\"{request.CustomerEmail}\"}}",
                CreatedAtUtc = DateTime.UtcNow
            };

            await outboxStore.SaveMessageAsync(outboxMessage, ct);

            // 3. Commit Transaction (Cả đơn hàng và Outbox message cùng được commit 100%)
            await tx.CommitAsync(ct);

            return orderId;
        }
        catch
        {
            // SessionTransaction auto-rollbacks on dispose if commit was not called
            throw;
        }
    }
}
