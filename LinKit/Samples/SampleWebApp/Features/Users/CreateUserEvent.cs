using LinKit.Core.Cqrs;
using LinKit.Core.Messaging;

namespace SampleWebApp.Features.Users
{
    [Message("user-events", RoutingKey = "user.created", QueueName = "email-service-queue")]
    public record UserCreatedEvent(int UserId, string Email) : ICommand;

    [CqrsHandler]
    public class UserCreatedEventHandler : ICommandHandler<UserCreatedEvent>
    {
        public Task HandleAsync(
            UserCreatedEvent command,
            CancellationToken cancellationToken = default
        )
        {
            throw new NotImplementedException();
        }
    }
}
