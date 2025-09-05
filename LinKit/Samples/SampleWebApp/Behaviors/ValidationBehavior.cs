using LinKit.Core.Cqrs;
using SampleWebApp.Contracts.Behaviors;
using System.ComponentModel.DataAnnotations;

namespace SampleWebApp.Behaviors;

[CqrsBehavior(typeof(IValidator), -10)]
public class ValidationBehavior<TRequest, TResponse>(IServiceProvider serviceProvider) : IPipelineBehavior<TRequest, TResponse>
 where TRequest : IQuery<TResponse>, IValidator
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    public async Task<TResponse> HandleAsync(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        var validator = _serviceProvider.GetService<IValidator<TRequest>>();

        try
        {
            if (validator is not null)
            {
                Console.WriteLine($"[VALIDATION] Found validator for {typeof(TRequest).Name}. Validating...");
                validator.Validate(request);
            }
            else
            {
                Console.WriteLine($"[VALIDATION] No validator found for {typeof(TRequest).Name}. Skipping.");
            }
        }
        catch (Exception ex) {
            throw new ValidationException(ex.Message, ex.InnerException ?? ex);
        }
        
        return await next();
    }
}
