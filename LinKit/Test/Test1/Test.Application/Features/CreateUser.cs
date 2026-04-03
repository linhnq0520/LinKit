using LinKit.Core.Cqrs;

namespace Test.Application.Features
{
    public class CreateUserCommand : ICommand<bool>
    {
    }

    //[CqrsHandler]
    public class CreateUserHandler : ICommandHandler<CreateUserCommand, bool>
    {
        public Task<bool> HandleAsync(CreateUserCommand request, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }
    }
}
