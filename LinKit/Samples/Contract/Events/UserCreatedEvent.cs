using LinKit.Core.Cqrs;
using LinKit.Core.Messaging;

namespace Contract.Events;

public interface IEvent : ICommand { }

[Message("user_events_exchange", RoutingKey = "user.created", QueueName = "email_service_queue")]
public record UserCreatedEvent(int UserId, string Email, string Name) : IEvent;
