using LinKit.Core.Cqrs;
using LinKit.Core.Endpoints;

namespace SampleWebApp.Features.Hello
{
    [ApiEndpoint(
        ApiMethod.Get,
        "/hello",
        Name = "SayHello",
        Summary = "Test api endpoint by say hello",
        RateLimitPolicy = "HelloLimit"
    )]
    public record HelloQuery(string Name) : IQuery<HelloReply>;
}
