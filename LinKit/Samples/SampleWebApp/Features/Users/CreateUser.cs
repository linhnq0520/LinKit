using Contract.Models;
using LinKit.Core.BackgroundJobs;
using LinKit.Core.Cqrs;
using LinKit.Core.Endpoints;
using SampleWebApp.Contracts.Behaviors;

namespace SampleWebApp.Features.Users;

[ApiEndpoint(ApiMethod.Post, "create-user")]
[BackgroundJob("AutoCreateUser")]
public class CreateUserCommand : ICommand
{
    public string Name { get; set; }
};

public record UpdateUserCommand(int Id, string Name) : ICommand, IAuditable;

[CqrsHandler]
public class CreateUser : ICommandHandler<CreateUserCommand>
{
    Task ICommandHandler<CreateUserCommand>.HandleAsync(
        CreateUserCommand command,
        CancellationToken cancellationToken
    )
    {
        UserDto user = new UserDto(1, command.Name);
        Console.WriteLine("Executed create user");
        return Task.FromResult(user);
    }
}

[CqrsHandler]
public class UpdateUser : ICommandHandler<UpdateUserCommand>
{
    public Task HandleAsync(
        UpdateUserCommand command,
        CancellationToken cancellationToken = default
    )
    {
        throw new NotImplementedException();
    }
}
