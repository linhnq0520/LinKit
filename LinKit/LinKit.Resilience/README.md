# LinKit.Resilience

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

A result-less overload takes `Func<CancellationToken, Task>` when the operation returns nothing.

### Options reference

| Property                  | Default       | Purpose                                                                                                                   |
| :------------------------ | :------------ | :------------------------------------------------------------------------------------------------------------------------ |
| `MaxRetries`              | `0`           | Additional attempts after the first failure; `0` disables retries.                                                        |
| `RetryDelay`              | `200 ms`      | Base delay between attempts, before backoff and jitter.                                                                   |
| `Backoff`                 | `Exponential` | How `RetryDelay` grows: `Constant` (1x), `Linear` (attempt + 1) or `Exponential` (2^attempt, capped at 2^20).             |
| `MaxRetryDelay`           | `30 s`        | Upper bound for a single retry delay, including the ±10% jitter.                                                          |
| `Timeout`                 | `null`        | Per-attempt timeout; `null` disables it.                                                                                  |
| `BreakerFailureThreshold` | `0`           | Consecutive failures that open the breaker; `0` disables the breaker.                                                     |
| `BreakerDurationSeconds`  | `30`          | Open window before a half-open probe is admitted.                                                                         |
| `ShouldRetry`             | `null`        | Decides whether an exception is transient. The default retries everything except caller cancellation and an open circuit. |

`ResilienceOptions.Validate()` throws `ArgumentOutOfRangeException` for a negative retry, delay, timeout or breaker value, and for `BreakerDurationSeconds <= 0` while the breaker is enabled. `ExecuteAsync` calls it on every invocation.

### Exceptions

| Exception                                     | When                                                                          |
| :-------------------------------------------- | :---------------------------------------------------------------------------- |
| `CircuitBreakerOpenException`                 | The breaker is open, so the action is not executed.                           |
| `TimeoutException`                            | An attempt exceeded `Timeout` and the action observed the cancellation token. |
| `OperationCanceledException`                  | The caller's token was cancelled; never retried.                              |
| `ArgumentException` / `ArgumentNullException` | `key` is blank, or `action`/`options` is `null`.                              |
| `ArgumentOutOfRangeException`                 | A numeric option is outside its supported range.                              |

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

## Testing

Every overload accepts a `TimeProvider`, so tests can drive delays and the breaker with `FakeTimeProvider` instead of sleeping. The breaker registry is process-wide and keyed by string, so use unique keys per test to keep state isolated.

## License

MIT. See the [repository license](https://github.com/linhnq0520/LinKit/blob/main/LICENSE).
