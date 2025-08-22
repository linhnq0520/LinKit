using LinKit.Core.Cqrs;
using LinKit.Core.Endpoints;
using SampleWebApp.Contracts.Behaviors;

namespace SampleWebApp.Features.Users;

[ApiEndpoint(ApiMethod.Post, "create-user")]
public record CreateUserCommand(string Name) : ICommand<UserDto>, IAuditable;

[CqrsHandler]
public class CreateUser : ICommandHandler<CreateUserCommand, UserDto>
{
    Task<UserDto> ICommandHandler<CreateUserCommand, UserDto>.HandleAsync(CreateUserCommand command, CancellationToken ct)
    {
        var user = new UserDto(1, command.Name); 
        return Task.FromResult(user);
    }
}
