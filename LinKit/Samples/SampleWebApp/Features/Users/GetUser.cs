using LinKit.Core.Cqrs;
using SampleWebApp.Contracts.Behaviors;

namespace SampleWebApp.Features.Users;

public record UserDto(int Id, string Name);

public record GetUserQuery(int Id) : IQuery<UserDto>, IValidator, IAuditable;

[CqrsHandler]
public class GetUserQueryHandler : IQueryHandler<GetUserQuery, UserDto>
{

    public Task<UserDto> HandleAsync(GetUserQuery query, CancellationToken ct = default)
    {
        var user = new UserDto(query.Id, "Awesome AOT User");
        return Task.FromResult(user);
    }
}
