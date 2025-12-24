using LinKit.Core.Cqrs;
using SampleWebApp.Contracts.Behaviors;
using System.ComponentModel.DataAnnotations;

namespace SampleWebApp.Behaviors;

[CqrsBehavior(typeof(IRequest), 1)]
public class ValidationBehavior1<TRequest, TResponse>(IServiceProvider serviceProvider)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    public async Task<TResponse> HandleAsync(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken
    )
    {
        IValidator<TRequest> validator = _serviceProvider.GetService<IValidator<TRequest>>();

        try
        {
            if (validator is not null)
            {
                Console.WriteLine(
                    $"[VALIDATION] Found validator for {typeof(TRequest).Name}. Validating..."
                );
                validator.Validate(request);
            }
            else
            {
                Console.WriteLine(
                    $"[VALIDATION] No validator found for {typeof(TRequest).Name}. Skipping."
                );
            }
        }
        catch (Exception ex)
        {
            throw new ValidationException(ex.Message, ex.InnerException ?? ex);
        }

        return await next();
    }
}
