using LinKit.Core.Cqrs;

namespace SampleWebApp.Features.Hello
{
    //[CqrsHandler]
    public class HelloHandler : IQueryHandler<HelloQuery, HelloReply>
    {
        public Task<HelloReply> HandleAsync(HelloQuery request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new HelloReply($"Hello {request.Name}"));
        }
    }
}
