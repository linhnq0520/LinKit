using LinKit.Core.Cqrs;
using Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Test1.Infarstructure
{
    public class UserEventHandler : INotificationHandler<UserEvent>
    {
        public Task HandleAsync(UserEvent notification, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }
    }
}
