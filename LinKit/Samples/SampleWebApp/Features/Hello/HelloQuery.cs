using LinKit.Core.Cqrs;
using LinKit.Core.Endpoints;

namespace SampleWebApp.Features.Hello
{
    [ApiEndpoint(ApiMethod.Get, "api/hello")]
    public record HelloQuery(string Name) : IQuery<HelloReply>;
}
