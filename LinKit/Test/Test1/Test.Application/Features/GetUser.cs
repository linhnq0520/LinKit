using LinKit.Core.Cqrs;

namespace Test.Application.Features
{
    public class GetUserQuery : IQuery<bool> { }

    //[CqrsHandler]
    public class GetUserHandler : IQueryHandler<GetUserQuery, bool>
    {
        public Task<bool> HandleAsync(
            GetUserQuery request,
            CancellationToken cancellationToken = default
        )
        {
            throw new NotImplementedException();
        }
    }
}
