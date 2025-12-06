using LinKit.Core.Cqrs;

namespace SampleWebApp.Features.Users.UpdateUser
{
    public record UpdateUserCommand(int Id, string Name, string Email) : ICommand<UpdateUserResposne>;
    public record UpdateUserResposne(int Id);
}
