# LinKit.Sagas

[![NuGet](https://img.shields.io/nuget/v/LinKit.Sagas.svg)](https://www.nuget.org/packages/LinKit.Sagas/)

`LinKit.Sagas` provides saga state/step contracts, a coordinator for one explicitly supplied step at a time, and a replaceable repository abstraction.

> The default repository is in-memory and volatile. This package does not provide a durable event log, automatic multi-step reverse orchestration, distributed locking, or exactly-once execution.

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

## Production persistence

Register a durable `ISagaRepository<TData>` implementation before `AddLinKitSagas<TData>` so the default registration does not replace it. Durable persistence alone does not make a saga operation exactly-once: protect step side effects with idempotency keys, and design recovery for crashes between the remote side effect and the state save. Repository concurrency/versioning, retries, saga history, and compensation-failure recovery are application/provider responsibilities.

## Native AOT

The coordinator uses statically typed generic contracts; validate serialization and the concrete repository in the published application. AOT support of a custom persistence provider is outside this package.

## License

MIT. See the [repository license](https://github.com/linhnq0520/LinKit/blob/main/LICENSE).
