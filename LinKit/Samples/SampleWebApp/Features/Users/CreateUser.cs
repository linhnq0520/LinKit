using Contract.Models;
using LinKit.Core.BackgroundJobs;
using LinKit.Core.Cqrs;
using LinKit.Core.Endpoints;
using SampleWebApp.Contracts.Behaviors;

namespace SampleWebApp.Features.Users;

[ApiEndpoint(ApiMethod.Post, "create-user")]
[BackgroundJob("AutoCreateUser")]
public class CreateUserCommand : BackgroundJobCommand, IAuditable, IValidator
{
    public string Name { get; set; }
};

[ApiEndpoint(ApiMethod.Put, "update-user")]
public record UpdateUserCommand(int Id, string Name) : ICommand, IAuditable;

[CqrsHandler]
public class CreateUser : ICommandHandler<CreateUserCommand>
{
    public Task<Unit> HandleAsync(
        CreateUserCommand request,
        CancellationToken cancellationToken = default
    )
    {
        UserDto user = new UserDto(1, request.Name);
        string embededData = request.EmbeddedData;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Executed create user");
        Console.WriteLine($"embededData == {embededData}");
        Console.ForegroundColor = ConsoleColor.White;
        return Task.FromResult(Unit.Value);
    }
}

[CqrsHandler]
public class UpdateUser : ICommandHandler<UpdateUserCommand>
{
    Task<Unit> IHandler<UpdateUserCommand, Unit>.HandleAsync(
        UpdateUserCommand request,
        CancellationToken cancellationToken
    )
    {
        throw new NotImplementedException();
    }
}
