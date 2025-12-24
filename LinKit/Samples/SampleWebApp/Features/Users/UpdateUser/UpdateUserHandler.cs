using LinKit.Core.Cqrs;

namespace SampleWebApp.Features.Users.UpdateUser
{
    public class UpdateUserHandler : ICommandHandler<UpdateUserCommand, UpdateUserResposne>
    {
        public async Task<UpdateUserResposne> HandleAsync(UpdateUserCommand request, CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            Domains.User user = request.ToUser();
            return user.ToUpdateUserResposne();
        }
    }
}
