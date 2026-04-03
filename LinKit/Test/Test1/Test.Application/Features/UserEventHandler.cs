using LinKit.Core.Cqrs;
using Shared;

namespace Test.Application.Features
{
    //[CqrsHandler]
    public class UserEventHandler : INotificationHandler<UserEvent>
    {
        public Task HandleAsync(UserEvent notification, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }
    }
}
