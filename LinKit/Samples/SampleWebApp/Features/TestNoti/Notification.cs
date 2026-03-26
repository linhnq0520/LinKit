using LinKit.Core.Cqrs;

namespace SampleWebApp.Features.TestNoti
{
    public class Notification : INotification { }

    [CqrsHandler]
    public class NotificationHandler1 : INotificationHandler<Notification>
    {
        public Task HandleAsync(
            Notification notification,
            CancellationToken cancellationToken = default
        )
        {
            throw new NotImplementedException();
        }
    }

    [CqrsHandler]
    public class NotificationHandler2 : INotificationHandler<Notification>
    {
        public Task HandleAsync(
            Notification notification,
            CancellationToken cancellationToken = default
        )
        {
            throw new NotImplementedException();
        }
    }
}
