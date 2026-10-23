using LinKit.Core.Cqrs;
using LinKit.Core.Endpoints;
using MonolithSample.Modules.Ordering.Events;

namespace MonolithSample.Modules.Ordering.Commands;

[HttpPost("api/orders/checkout", Summary = "Đặt hàng và thanh toán")]
public record PlaceOrderCommand(string CustomerEmail, decimal TotalAmount) : ICommand<Guid>;

[CqrsHandler]
public class PlaceOrderCommandHandler(IMediator mediator) : ICommandHandler<PlaceOrderCommand, Guid>
{
    public async Task<Guid> HandleAsync(PlaceOrderCommand request, CancellationToken cancellationToken = default)
    {
        var orderId = Guid.NewGuid();

        // In-Process Domain Event Publish tới tất cả subscribers
        await mediator.PublishAsync(
            new OrderPlacedEvent(orderId, request.CustomerEmail, request.TotalAmount),
            strategy: PublishStrategy.Sequential,
            ct: cancellationToken
        );

        return orderId;
    }
}
