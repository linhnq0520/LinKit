using LinKit.Core.Cqrs;
using LinKit.Core.Messaging;

namespace MicroserviceSample.SharedContracts.Events;

[Message(
    "orders-exchange",
    RoutingKey = "order.submitted",
    QueueName = "inventory-orders-queue",
    MaxRetries = 3,
    RetryIntervalMs = 500,
    DeadLetterQueue = "orders-dlq"
)]
public record OrderSubmittedEvent(
    Guid OrderId,
    string CustomerId,
    string ProductCode,
    int Quantity,
    decimal TotalPrice
) : ICommand;
