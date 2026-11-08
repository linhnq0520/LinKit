using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;
using LinKit.Core.Cqrs;
using LinKit.Core.Messaging;

namespace LinKit.Core.Diagnostics;

/// <summary>
/// Process-wide OpenTelemetry instrumentation source for LinKit: one <see cref="System.Diagnostics.ActivitySource"/> for
/// traces and one <see cref="Meter"/> for metrics. Subscribe to <see cref="SourceName"/> from OpenTelemetry to collect
/// them.
/// </summary>
public static class LinKitDiagnostics
{
    /// <summary>
    /// Name shared by the activity source and the meter, and the value to pass to
    /// <c>AddSource("LinKit")</c> / <c>AddMeter("LinKit")</c>.
    /// </summary>
    public const string SourceName = "LinKit";

    /// <summary>
    /// Version of the assembly that publishes the instrumentation, reported to collectors alongside the spans.
    /// </summary>
    public static readonly string SourceVersion =
        typeof(LinKitDiagnostics).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// Activity source used to emit one span per dispatched request.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(SourceName, SourceVersion);

    /// <summary>
    /// Meter that owns the request counters and histograms.
    /// </summary>
    public static readonly Meter Meter = new(SourceName, SourceVersion);

    /// <summary>
    /// Number of CQRS requests handled, tagged with the request type name.
    /// </summary>
    public static readonly Counter<long> RequestExecutionCounter = Meter.CreateCounter<long>(
        "linkit.requests.total",
        description: "Total number of CQRS requests handled"
    );

    /// <summary>
    /// Duration of CQRS request execution in milliseconds, tagged with the request type name.
    /// </summary>
    public static readonly Histogram<double> RequestExecutionDuration =
        Meter.CreateHistogram<double>(
            "linkit.requests.duration_ms",
            unit: "ms",
            description: "Duration of CQRS request execution in milliseconds"
        );
}

/// <summary>
/// Propagates the W3C trace context (<c>traceparent</c> and <c>tracestate</c>) through message headers, so a trace
/// started by a producer continues in the consuming service.
/// </summary>
public static class W3CTraceContext
{
    /// <summary>
    /// Name of the header that carries the W3C <c>traceparent</c> value.
    /// </summary>
    public const string TraceParentHeader = "traceparent";

    /// <summary>
    /// Name of the header that carries the W3C <c>tracestate</c> value.
    /// </summary>
    public const string TraceStateHeader = "tracestate";

    /// <summary>
    /// Writes the current (or supplied) activity context into <paramref name="headers"/>. Does nothing when there is no
    /// active activity, which keeps the headers untouched outside of a traced operation.
    /// </summary>
    /// <param name="headers">Headers of the message being produced.</param>
    /// <param name="activity">Activity to propagate; defaults to <see cref="Activity.Current"/>.</param>
    public static void Inject(MessageHeaders headers, Activity? activity = null)
    {
        var current = activity ?? Activity.Current;
        if (current == null)
            return;

        headers[TraceParentHeader] = current.Id ?? string.Empty;
        if (!string.IsNullOrEmpty(current.TraceStateString))
        {
            headers[TraceStateHeader] = current.TraceStateString;
        }
    }

    /// <summary>
    /// Reads the trace context stored in <paramref name="headers"/> so a consumer can start a child span.
    /// </summary>
    /// <param name="headers">Headers of the message being consumed.</param>
    /// <returns>
    /// The parsed <see cref="ActivityContext"/>, or <see langword="default"/> when the headers contain no valid
    /// <c>traceparent</c> value.
    /// </returns>
    public static ActivityContext Extract(MessageHeaders headers)
    {
        if (
            headers.TryGetValue(TraceParentHeader, out var tpObj)
            && tpObj is string traceParent
            && !string.IsNullOrEmpty(traceParent)
        )
        {
            string? traceState = headers.TryGetValue(TraceStateHeader, out var tsObj)
                ? tsObj?.ToString()
                : null;
            if (ActivityContext.TryParse(traceParent, traceState, out var context))
            {
                return context;
            }
        }
        return default;
    }
}

/// <summary>
/// Pipeline behavior that wraps every request in an activity and records the request counter and duration histogram,
/// giving one span per request with success or error status. Register it as the outermost behavior so the span covers
/// the other behaviors as well.
/// </summary>
/// <typeparam name="TRequest">Request type being processed.</typeparam>
/// <typeparam name="TResponse">Response type produced by the request.</typeparam>
public class TracingPipelineBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    /// <summary>
    /// Starts the span, invokes <paramref name="next"/> and records the outcome.
    /// </summary>
    /// <param name="request">The request being processed.</param>
    /// <param name="next">Delegate that continues the pipeline.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>The response of the request.</returns>
    public async Task<TResponse> HandleAsync(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken
    )
    {
        string requestName = typeof(TRequest).Name;
        using var activity = LinKitDiagnostics.ActivitySource.StartActivity(
            $"LinKit.Handle.{requestName}",
            ActivityKind.Internal
        );

        activity?.SetTag("linkit.request.type", typeof(TRequest).FullName);
        activity?.SetTag(
            "linkit.request.is_command",
            typeof(ICommand).IsAssignableFrom(typeof(TRequest))
        );

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
            activity?.AddEvent(
                new ActivityEvent(
                    "exception",
                    tags: new ActivityTagsCollection
                    {
                        { "exception.message", ex.Message },
                        { "exception.type", ex.GetType().FullName ?? "Unknown" },
                    }
                )
            );
            throw;
        }
        finally
        {
            sw.Stop();
            LinKitDiagnostics.RequestExecutionCounter.Add(
                1,
                new KeyValuePair<string, object?>("request", requestName)
            );
            LinKitDiagnostics.RequestExecutionDuration.Record(
                sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("request", requestName)
            );
        }
    }
}
