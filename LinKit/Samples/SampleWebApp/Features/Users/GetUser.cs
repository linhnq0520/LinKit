using Contract.Models;
using LinKit.Core.Cqrs;
using LinKit.Core.Endpoints;
using LinKit.Grpc;
using SampleWebApp.Contracts.Behaviors;
using SampleWebApp.Grpc.Users;

namespace SampleWebApp.Features.Users;

[ApiEndpoint(ApiMethod.Get, "get-user/{id}")]
[GrpcEndpoint(typeof(UserGrpcService.UserGrpcServiceBase), "GetUser")]
public record GetUserQuery : IQuery<UserDto>, IValidator, IAuditable
{
    public GetUserQuery() { }

    public GetUserQuery(int id)
    {
        Id = id;
    }

    [FromQuery]
    [FromRoute]
    public int Id { get; set; }
};

[ApiEndpoint(ApiMethod.Get, "get-users")]
[GrpcEndpoint(typeof(UserGrpcService.UserGrpcServiceBase), "GetUsers")]
public record GetUsersQuery : IQuery<UsersDto>, IValidator, IAuditable { };

[CqrsHandler]
public class GetUserQueryHandler : IQueryHandler<GetUserQuery, UserDto>
{
    public Task<UserDto> HandleAsync(GetUserQuery query, CancellationToken ct = default)
    {
        var user = new UserDto(query.Id, "Awesome AOT User");
        return Task.FromResult(user);
    }
}

[CqrsHandler]
public class GetUsersQueryHandler : IQueryHandler<GetUsersQuery, UsersDto>
{
    public Task<UsersDto> HandleAsync(GetUsersQuery query, CancellationToken ct = default)
    {
        var headerUser = GrpcUtils.GetHeaderValue<UserDto>("key1", AppJsonContext.Default);
        //Console.WriteLine(JsonSerializer.Serialize(header, AppJsonContext.Default.Metadata));
        var user1 = new UserDto(1, "Awesome AOT User");
        var user2 = new UserDto(2, "Awesome AOT User");
        var list = new List<UserDto>() { user1, user2, headerUser };
        var res = new UsersDto { Users = list };

        return Task.FromResult(res);
    }
}
