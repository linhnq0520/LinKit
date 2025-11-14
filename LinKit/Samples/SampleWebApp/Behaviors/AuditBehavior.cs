using LinKit.Core.Cqrs;
using SampleWebApp.Contracts.Behaviors;

namespace SampleWebApp.Behaviors;

[CqrsBehavior(typeof(IAuditable), 2)]
public class AuditBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>, IAuditable
{
    public async Task<TResponse> HandleAsync(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken
    )
    {
        Console.WriteLine(
            $"[AUDIT] User 'system' is attempting to execute {typeof(TRequest).Name}"
        );
        return await next();
    }
}
