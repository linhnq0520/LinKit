using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;
using LinKit.Core.Cqrs;
using LinKit.Core.Messaging;

namespace LinKit.Core.Diagnostics;

public static class LinKitDiagnostics
{
    public const string SourceName = "LinKit";
    public static readonly string SourceVersion =
        typeof(LinKitDiagnostics).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static readonly ActivitySource ActivitySource = new(SourceName, SourceVersion);
    public static readonly Meter Meter = new(SourceName, SourceVersion);

    public static readonly Counter<long> RequestExecutionCounter = Meter.CreateCounter<long>(
        "linkit.requests.total",
        description: "Total number of CQRS requests handled"
    );

    public static readonly Histogram<double> RequestExecutionDuration = Meter.CreateHistogram<double>(
        "linkit.requests.duration_ms",
        unit: "ms",
        description: "Duration of CQRS request execution in milliseconds"
    );
}

public static class W3CTraceContext
{
    public const string TraceParentHeader = "traceparent";
    public const string TraceStateHeader = "tracestate";

    public static void Inject(MessageHeaders headers, Activity? activity = null)
    {
        var current = activity ?? Activity.Current;
        if (current == null) return;

        headers[TraceParentHeader] = current.Id ?? string.Empty;
        if (!string.IsNullOrEmpty(current.TraceStateString))
        {
            headers[TraceStateHeader] = current.TraceStateString;
        }
    }

    public static ActivityContext Extract(MessageHeaders headers)
    {
        if (headers.TryGetValue(TraceParentHeader, out var tpObj) && tpObj is string traceParent && !string.IsNullOrEmpty(traceParent))
        {
            string? traceState = headers.TryGetValue(TraceStateHeader, out var tsObj) ? tsObj?.ToString() : null;
            if (ActivityContext.TryParse(traceParent, traceState, out var context))
            {
                return context;
            }
        }
        return default;
    }
}

public class TracingPipelineBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> HandleAsync(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        string requestName = typeof(TRequest).Name;
        using var activity = LinKitDiagnostics.ActivitySource.StartActivity($"LinKit.Handle.{requestName}", ActivityKind.Internal);

        activity?.SetTag("linkit.request.type", typeof(TRequest).FullName);
        activity?.SetTag("linkit.request.is_command", typeof(ICommand).IsAssignableFrom(typeof(TRequest)));

        var sw = Stopwatch.StartNew();
        try
        {
            var response = await next();
            activity?.SetStatus(ActivityStatusCode.Ok);
            return response;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
            {
                { "exception.message", ex.Message },
                { "exception.type", ex.GetType().FullName ?? "Unknown" }
            }));
            throw;
        }
        finally
        {
            sw.Stop();
            LinKitDiagnostics.RequestExecutionCounter.Add(1, new KeyValuePair<string, object?>("request", requestName));
            LinKitDiagnostics.RequestExecutionDuration.Record(sw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("request", requestName));
        }
    }
}
