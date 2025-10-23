using Contract.Models;
using LinKit.Core.BackgroundJobs;
using LinKit.Core.Cqrs;
using LinKit.Core.Endpoints;
using SampleWebApp.Contracts.Behaviors;

namespace SampleWebApp.Features.Users;

[ApiEndpoint(ApiMethod.Post, "create-user")]
[BackgroundJob("AutoCreateUser")]
public class CreateUserCommand : BackgroundJobCommand
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
        string embededData = command.EmbeddedData;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Executed create user");
        Console.WriteLine($"embededData == {embededData}");
        Console.ForegroundColor = ConsoleColor.White;
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
