using LinKit.Core.Cqrs;
using LinKit.Core.Messaging;
using SampleWebApp.Contracts.Behaviors;

namespace SampleWebApp.Features.Users
{
    [Message("user-events", RoutingKey = "user.created", QueueName = "email-service-queue")]
    public record UserCreatedEvent(int UserId, string Email) : ICommand, IAuditable, IValidator;

    [CqrsHandler]
    public class UserCreatedEventHandler : ICommandHandler<UserCreatedEvent>
    {
        Task<Unit> IHandler<UserCreatedEvent, Unit>.HandleAsync(
            UserCreatedEvent request,
            CancellationToken cancellationToken
        )
        {
            throw new NotImplementedException();
        }
    }
}
