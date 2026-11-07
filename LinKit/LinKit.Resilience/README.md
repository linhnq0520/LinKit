# LinKit.Resilience

[![NuGet](https://img.shields.io/nuget/v/LinKit.Resilience.svg)](https://www.nuget.org/packages/LinKit.Resilience/)

`LinKit.Resilience` provides an explicit async execution pipeline with retries, per-attempt timeout, optional circuit breaker, configurable transient-error filtering, and `TimeProvider` support for deterministic tests.

## Install

```shell
dotnet add package LinKit.Resilience
```

## Execute an operation

```csharp
using LinKit.Core.Resilience;
using LinKit.Resilience;

var options = new ResilienceOptions
{
    MaxRetries = 2, // additional attempts after the first call
    RetryDelay = TimeSpan.FromMilliseconds(200),
    MaxRetryDelay = TimeSpan.FromSeconds(3),
    Backoff = BackoffType.Exponential,
    Timeout = TimeSpan.FromSeconds(5),
    BreakerFailureThreshold = 5,
    BreakerDurationSeconds = 30,
    ShouldRetry = exception => exception is HttpRequestException
};

var response = await ResiliencePipeline.ExecuteAsync(
    "inventory-api",
    cancellationToken => inventoryClient.GetAsync(productId, cancellationToken),
    options,
    cancellationToken);
```

The order is retry loop, circuit-breaker check, per-attempt timeout, then the action. A successful attempt resets the breaker (see [Circuit breaker generations](#circuit-breaker-generations)). `MaxRetries = 2` permits up to three attempts. A zero breaker threshold disables the breaker.

## Circuit breaker generations

The circuit breaker exposes generation-aware overloads:

```csharp
bool CanExecute(int durationSeconds, out long generation);
void OnSuccess(long generation);
```

A slow success admitted before the breaker opened can no longer close it: stale generations are ignored, and only the matching half-open probe closes the breaker. `ResiliencePipeline` uses the new overloads internally; the old parameterless members are kept for compatibility. Half-open probes that never report back are re-admitted after the open duration instead of wedging the breaker.

## Cancellation and timeout

Caller cancellation is propagated and is not retried. A timeout cancels the token passed to the action and reports `TimeoutException` when the action responds to cancellation. An action that ignores its token may continue running after the pipeline times out; do not use this as a mechanism to forcibly stop non-cooperative work.

Retries can repeat side effects. Use only for operations that are idempotent or protected by an idempotency key, and configure `ShouldRetry` narrowly for transient errors. Circuit breaker state is held in a process-wide registry keyed by string; use unique, stable keys for independent dependencies.

## DI registration

`AddLinKitResilience()` is available for service registration/composition. The pipeline itself is invoked explicitly with `ResiliencePipeline.ExecuteAsync`; adding the package does not automatically wrap CQRS handlers or apply attributes to requests.

## Native AOT

The pipeline uses delegates and does not require dynamic code generation in its direct execution path. Validate the full app and provider composition with trimming/AOT analyzers and publish for the deployment RID.

## License

MIT. See the [repository license](https://github.com/linhnq0520/LinKit/blob/main/LICENSE).
