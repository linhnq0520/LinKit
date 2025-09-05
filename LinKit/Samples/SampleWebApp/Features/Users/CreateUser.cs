using Contract.Models;
using LinKit.Core.Cqrs;
using LinKit.Core.Endpoints;
using SampleWebApp.Contracts.Behaviors;

namespace SampleWebApp.Features.Users;

[ApiEndpoint(ApiMethod.Post, "create-user")]
public record CreateUserCommand(string Name) : ICommand<UserDto>, IAuditable;
public record UpdateUserCommand(int Id,string Name) : ICommand, IAuditable;

[CqrsHandler]
public class CreateUser : ICommandHandler<CreateUserCommand, UserDto>
{
    Task<UserDto> ICommandHandler<CreateUserCommand, UserDto>.HandleAsync(CreateUserCommand command, CancellationToken ct)
    {
        var user = new UserDto(1, command.Name); 
        return Task.FromResult(user);
    }
}

[CqrsHandler]
public class UpdateUser : ICommandHandler<UpdateUserCommand>
{
    public Task HandleAsync(UpdateUserCommand command, CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }

}
