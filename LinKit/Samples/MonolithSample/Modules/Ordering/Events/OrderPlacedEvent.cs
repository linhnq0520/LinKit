using LinKit.Core.Cqrs;

namespace MonolithSample.Modules.Ordering.Events;

public record OrderPlacedEvent(Guid OrderId, string CustomerEmail, decimal TotalAmount) : INotification;
