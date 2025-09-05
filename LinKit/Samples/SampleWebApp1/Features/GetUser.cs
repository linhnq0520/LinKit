using Contract.Models;
using LinKit.Core.Cqrs;
using LinKit.Core.Endpoints;
using LinKit.Grpc;

namespace SampleWebApp1.Features
{
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
            try
            {
                var model = query.ToGetUserById();
                return await _grpcMediator.QueryAsync<GetUserById, UserDto>(model);
            }
            catch (Exception ex)
            {
                throw;
            }

        }
    }
}
