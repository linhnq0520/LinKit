# LinKit.Sagas

`LinKit.Sagas` provides saga state/step contracts, a coordinator for one explicitly supplied step at a time, and a replaceable repository abstraction.

> The default repository is in-memory and volatile. This package does not provide a durable event log, distributed locking, or exactly-once execution.

## Install

```shell
dotnet add package LinKit.Sagas
```

## Define state and a step

```csharp
using LinKit.Sagas;

public sealed class CheckoutSagaData : SagaData
{
    public Guid OrderId { get; set; }
    public bool ReservationCompleted { get; set; }
}

public sealed class ReserveStockStep(IStockService stock) :
    ISagaStep<CheckoutSagaData, OrderSubmitted>
{
    public async Task ExecuteAsync(
        CheckoutSagaData data,
        OrderSubmitted message,
        CancellationToken cancellationToken = default)
    {
        await stock.ReserveAsync(message.OrderId, cancellationToken);
        data.OrderId = message.OrderId;
        data.ReservationCompleted = true;
    }

    public async Task CompensateAsync(
        CheckoutSagaData data,
        OrderSubmitted message,
        CancellationToken cancellationToken = default)
    {
        if (data.ReservationCompleted)
        {
            await stock.ReleaseAsync(data.OrderId, cancellationToken);
            data.ReservationCompleted = false;
        }
    }
}
```

## Register and execute

```csharp
builder.Services.AddLinKitSagas<CheckoutSagaData>();
builder.Services.AddScoped<ReserveStockStep>();

var app = builder.Build();
var coordinator = app.Services.GetRequiredService<SagaCoordinator<CheckoutSagaData>>();
var step = app.Services.GetRequiredService<ReserveStockStep>();

await coordinator.ExecuteStepAsync(
    correlationId,
    submittedEvent,
    step,
    nextState: "StockReserved",
    cancellationToken);
```

The coordinator loads state by correlation ID or creates a new data object, runs the supplied step, updates `CurrentState`, and saves. If execution throws, it invokes that same step's compensation with a separate bounded token, records a compensated state when possible, and rethrows the original exception.

## Concurrency and idempotent compensation

`ISagaData` carries two fields that the coordinator and the repository rely on:

- `Version` — an optimistic concurrency token. `ISagaRepository<TData>.SaveAsync` must increment it and reject a save whose token no longer matches the persisted value by throwing `SagaConcurrencyException`. Two workers on the same correlation ID therefore cannot interleave; the loser reloads the state and decides whether the step should be retried.
- `CompensatedSteps` — the ledger of steps that already ran their compensation. It is persisted with the state, so a redelivered saga (at-least-once transports redeliver) never undoes the same side effect twice. A compensation that throws is _not_ recorded, so a later retry attempts it again.

The in-memory repository implements both, so the same code paths are exercised in tests.

```csharp
public sealed class DurableSagaRepository<TData> : ISagaRepository<TData> where TData : class, ISagaData
{
    public Task SaveAsync(TData data, CancellationToken cancellationToken = default)
    {
        // UPDATE saga SET ..., version = @new, compensated_steps = @steps
        //  WHERE correlation_id = @id AND version = @data.Version
        // 0 rows affected -> throw new SagaConcurrencyException(data.CorrelationId, data.Version, actualVersion).
        throw new NotImplementedException();
    }

    // FindByCorrelationIdAsync and GetStuckAsync are the other two members.
}
```

## Stuck sagas

`ISagaRepository<TData>.GetStuckAsync(TimeSpan olderThan)` returns every saga whose `UpdatedAtUtc` is older than the threshold — the signature of a step that threw after its side effect ran, or of a process that died mid-step. `AddLinKitSagas<TData>()` registers a background monitor that logs a warning with the correlation IDs whenever that set is non-empty.

```csharp
builder.Services.AddLinKitSagas<CheckoutSagaData>(options =>
{
    options.StuckAfter = TimeSpan.FromMinutes(5);
    options.StuckSagaCheckInterval = TimeSpan.FromMinutes(1);
    options.EnableStuckSagaMonitor = true;      // default
    options.CompensationTimeout = TimeSpan.FromSeconds(30); // default
});
```

## Production persistence

Register a durable `ISagaRepository<TData>` implementation before `AddLinKitSagas<TData>` so the default registration does not replace it. Because the in-memory fallback loses every in-flight saga on restart, resolution throws in the `Production` environment unless you opt in explicitly:

```csharp
builder.Services.AddLinKitSagas<CheckoutSagaData>(options => options.AllowInMemoryInProduction = true);
```

Durable persistence alone does not make a saga operation exactly-once: protect step side effects with idempotency keys, and design recovery for crashes between the remote side effect and the state save. Saga history and compensation-failure recovery remain application/provider responsibilities.

## Native AOT

The coordinator uses statically typed generic contracts; validate serialization and the concrete repository in the published application. AOT support of a custom persistence provider is outside this package.

## License

MIT. See the [repository license](https://github.com/linhnq0520/LinKit/blob/main/LICENSE).
