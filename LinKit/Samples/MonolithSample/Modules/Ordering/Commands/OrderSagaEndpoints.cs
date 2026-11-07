using LinKit.Core.Cqrs;
using LinKit.Core.Endpoints;
using LinKit.Sagas;

namespace MonolithSample.Modules.Ordering.Commands;

/// <summary>
/// Demo use-case LinKit.Sagas:
/// Điều phối giao dịch phân tán (Saga Orchestration) có cơ chế bù trừ (Compensation) khi gặp sự cố!
/// </summary>
public class OrderSagaState : SagaData
{
    public decimal Amount { get; set; }
    public bool InventoryReserved { get; set; }
    public bool PaymentProcessed { get; set; }
}

public record ReserveInventoryEvent(decimal Amount);
public record ProcessPaymentEvent(decimal Amount, bool SimulateFailure);

public class ReserveInventoryStep : ISagaStep<OrderSagaState, ReserveInventoryEvent>
{
    public Task ExecuteAsync(OrderSagaState data, ReserveInventoryEvent @event, CancellationToken cancellationToken = default)
    {
        data.InventoryReserved = true;
        return Task.CompletedTask;
    }

    public Task CompensateAsync(OrderSagaState data, ReserveInventoryEvent @event, CancellationToken cancellationToken = default)
    {
        // Bù trừ: Trả hàng lại kho nếu bước sau thất bại!
        data.InventoryReserved = false;
        return Task.CompletedTask;
    }
}

public class ProcessPaymentStep : ISagaStep<OrderSagaState, ProcessPaymentEvent>
{
    public Task ExecuteAsync(OrderSagaState data, ProcessPaymentEvent @event, CancellationToken cancellationToken = default)
    {
        if (@event.SimulateFailure)
        {
            throw new InvalidOperationException("Cổng thanh toán từ chối giao dịch!");
        }
        data.PaymentProcessed = true;
        return Task.CompletedTask;
    }

    public Task CompensateAsync(OrderSagaState data, ProcessPaymentEvent @event, CancellationToken cancellationToken = default)
    {
        // Bù trừ: Hoàn lại tiền nếu cần
        data.PaymentProcessed = false;
        return Task.CompletedTask;
    }
}

[HttpPost("api/sagas/checkout", Summary = "Thực hiện luồng thanh toán phân tán có bù trừ (Saga Compensation)")]
public record ExecuteOrderSagaCommand(decimal Amount, bool SimulatePaymentFailure) : ICommand<string>;

[CqrsHandler]
public class ExecuteOrderSagaCommandHandler(SagaCoordinator<OrderSagaState> coordinator) 
    : ICommandHandler<ExecuteOrderSagaCommand, string>
{
    public async Task<string> HandleAsync(ExecuteOrderSagaCommand request, CancellationToken ct = default)
    {
        var correlationId = Guid.NewGuid();

        try
        {
            // Thực thi chuỗi các bước Saga với cơ chế tự động bù trừ ngược (LIFO compensation) nếu có bước lỗi
            var state = await coordinator.Begin(correlationId)
                .Step(new ReserveInventoryStep(), new ReserveInventoryEvent(request.Amount), "InventoryReserved")
                .Step(new ProcessPaymentStep(), new ProcessPaymentEvent(request.Amount, request.SimulatePaymentFailure), "Completed")
                .ExecuteAsync(ct);

            return $"Saga {correlationId} Completed Successfully! State: {state.CurrentState}";
        }
        catch (Exception ex)
        {
            return $"Saga {correlationId} Failed and Compensated. Error: {ex.Message}";
        }
    }
}

