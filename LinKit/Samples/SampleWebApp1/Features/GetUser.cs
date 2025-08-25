using System.ComponentModel.DataAnnotations;
using LinKit.Core.Cqrs;
using LinKit.Core.Endpoints;
using LinKit.Core.Grpc;
using SampleWebApp.Grpc.Users;

namespace SampleWebApp1.Features
{
    public class UserDto
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }

    [GrpcClient(typeof(UserGrpcService.UserGrpcServiceClient), "GetUserAsync")]
    [ApiEndpoint(ApiMethod.Get, "get-user/{id}")]
    public record GetUserQuery : IQuery<UserDto?>
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

    [CqrsHandler]
    public class GetUserQueryHandler(IGrpcMediator grpcMediator)
        : IQueryHandler<GetUserQuery, UserDto>
    {
        private readonly IGrpcMediator _grpcMediator = grpcMediator;

        public async Task<UserDto> HandleAsync(GetUserQuery query, CancellationToken ct = default)
        {
            return await _grpcMediator.QueryAsync<GetUserQuery, UserDto>(query);
        }
    }
}
