using System.Diagnostics;
using LinKit.Core.Cqrs;
using Microsoft.Extensions.Logging;

namespace MonolithSample.Behaviors;

[CqrsBehavior(typeof(object), 1)]
public class LoggingBehavior<TRequest, TResponse>(ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> HandleAsync(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var requestName = typeof(TRequest).Name;
        logger.LogInformation("[Pipeline] Bắt đầu xử lý {RequestName}", requestName);
        var sw = Stopwatch.StartNew();

        var response = await next();

        sw.Stop();
        logger.LogInformation("[Pipeline] Hoàn tất {RequestName} trong {ElapsedMilliseconds} ms", requestName, sw.ElapsedMilliseconds);
        return response;
    }
}
