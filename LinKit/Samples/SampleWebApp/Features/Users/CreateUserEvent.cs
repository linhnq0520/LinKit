using LinKit.Core.BackgroundJobs;
using LinKit.Core.Cqrs;
using LinKit.Core.Endpoints;
using LinKit.Core.Messaging;

namespace SampleWebApp.Features.Users
{
    [Message("user-events", RoutingKey = "user.created", QueueName = "email-service-queue")]
    [ApiEndpoint(
        ApiMethod.Post,
        "create-event",
        Name = "create event",
        Summary = "create a new event",
        Roles = "Admin",
        MediatorKey = "IdentityMediator", // Uses [FromKeyedServices("IdentityMediator")]
        RateLimitPolicy = "Strict"
    )]
    //[ApplyBehavior(typeof(ValidationBehavior1<,>))]
    public record UserCreatedEvent(int UserId, string Email) : ICommand<bool>;

    [CqrsHandler]
    public class UserCreatedEventHandler : ICommandHandler<UserCreatedEvent, bool>
    {
        public Task<bool> HandleAsync(
            UserCreatedEvent request,
            CancellationToken cancellationToken = default
        )
        {
            throw new NotImplementedException();
        }
    }

    public class TestJob : BackgroundJobCommand { }

    [CqrsHandler]
    public class TestJobHandler : ICommandHandler<TestJob>
    {
        public Task<Unit> HandleAsync(TestJob request, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }
    }
}
