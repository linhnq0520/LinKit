using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LinKit.Sagas;

/// <summary>
/// Persisted saga state. Application sagas normally derive from <see cref="SagaData"/>.
/// </summary>
public interface ISagaData
{
    Guid CorrelationId { get; set; }

    string CurrentState { get; set; }

    DateTime CreatedAtUtc { get; set; }

    DateTime UpdatedAtUtc { get; set; }

    /// <summary>
    /// Optimistic concurrency token. <see cref="ISagaRepository{TData}.SaveAsync"/> increments it on success and must
    /// reject a save whose token no longer matches the persisted one, so two workers cannot interleave on the same
    /// correlation id.
    /// </summary>
    int Version { get; set; }

    /// <summary>
    /// Names of the steps that already ran their compensation. The ledger is persisted with the rest of the state so a
    /// retried saga never compensates the same step twice.
    /// </summary>
    ISet<string> CompensatedSteps { get; set; }
}

public class SagaData : ISagaData
{
    public Guid CorrelationId { get; set; } = Guid.NewGuid();
    public string CurrentState { get; set; } = "Initial";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public int Version { get; set; }
    public ISet<string> CompensatedSteps { get; set; } =
        new HashSet<string>(StringComparer.Ordinal);
}

internal static class SagaDataState
{
    /// <summary>
    /// Returns the compensation ledger, materializing it when a durable store hydrated a state object without one.
    /// </summary>
    internal static ISet<string> CompensatedStepsOf(this ISagaData data)
    {
        var steps = data.CompensatedSteps;
        if (steps is null)
        {
            steps = new HashSet<string>(StringComparer.Ordinal);
            data.CompensatedSteps = steps;
        }

        return steps;
    }
}

/// <summary>
/// Thrown when a saga save loses an optimistic concurrency race: another worker advanced the state between the load
/// and the save. The caller must reload the state and decide whether the step should be retried.
/// </summary>
public sealed class SagaConcurrencyException : Exception
{
    public SagaConcurrencyException(Guid correlationId, int expectedVersion, int actualVersion)
        : base(
            $"Saga '{correlationId}' was modified concurrently: this worker holds version {expectedVersion} but the "
                + $"store already contains version {actualVersion}. Reload the state and retry the step."
        )
    {
        CorrelationId = correlationId;
        ExpectedVersion = expectedVersion;
        ActualVersion = actualVersion;
    }

    public Guid CorrelationId { get; }

    /// <summary>The version the caller's state object claims to be.</summary>
    public int ExpectedVersion { get; }

    /// <summary>The version currently persisted.</summary>
    public int ActualVersion { get; }
}

public interface ISagaStep<TData, in TEvent>
    where TData : ISagaData
{
    string Name => GetType().Name;

    Task ExecuteAsync(TData data, TEvent @event, CancellationToken cancellationToken = default);
    Task CompensateAsync(TData data, TEvent @event, CancellationToken cancellationToken = default);
}

public interface ISagaExecutableStep<TData>
    where TData : ISagaData
{
    string Name { get; }
    string NextState { get; }
    Task ExecuteAsync(TData data, CancellationToken cancellationToken = default);
    Task CompensateAsync(TData data, CancellationToken cancellationToken = default);
}

internal sealed class SagaExecutableStep<TData, TEvent>(
    ISagaStep<TData, TEvent> step,
    TEvent @event,
    string nextState
) : ISagaExecutableStep<TData>
    where TData : ISagaData
{
    public string Name => step.Name;
    public string NextState => nextState;

    public Task ExecuteAsync(TData data, CancellationToken cancellationToken = default) =>
        step.ExecuteAsync(data, @event, cancellationToken);

    public Task CompensateAsync(TData data, CancellationToken cancellationToken = default) =>
        step.CompensateAsync(data, @event, cancellationToken);
}

public interface ISagaRepository<TData>
    where TData : ISagaData
{
    Task<TData?> FindByCorrelationIdAsync(
        Guid correlationId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Persists the saga state. Implementations must increment <see cref="ISagaData.Version"/> and reject a save whose
    /// version no longer matches the stored one by throwing <see cref="SagaConcurrencyException"/>.
    /// </summary>
    Task SaveAsync(TData data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every saga whose <see cref="ISagaData.UpdatedAtUtc"/> is older than <paramref name="olderThan"/>, so a
    /// startup check or background monitor can surface work that stopped advancing.
    /// </summary>
    Task<IReadOnlyList<TData>> GetStuckAsync(
        TimeSpan olderThan,
        CancellationToken cancellationToken = default
    );
}

public class InMemorySagaRepository<TData> : ISagaRepository<TData>
    where TData : class, ISagaData
{
    private readonly ConcurrentDictionary<Guid, Entry> _store = new();

    private sealed class Entry(TData data)
    {
        public TData Data { get; set; } = data;

        public int Version { get; set; }

        public object SyncRoot { get; } = new();
    }

    public Task<TData?> FindByCorrelationIdAsync(
        Guid correlationId,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<TData?>(
            _store.TryGetValue(correlationId, out var entry) ? entry.Data : null
        );
    }

    public Task SaveAsync(TData data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        cancellationToken.ThrowIfCancellationRequested();

        var entry = _store.GetOrAdd(data.CorrelationId, _ => new Entry(data));

        try
        {
            lock (entry.SyncRoot)
            {
                if (data.Version != entry.Version)
                {
                    throw new SagaConcurrencyException(
                        data.CorrelationId,
                        data.Version,
                        entry.Version
                    );
                }

                entry.Version = data.Version + 1;
                data.Version = entry.Version;
                data.UpdatedAtUtc = DateTime.UtcNow;
                entry.Data = data;
            }
        }
        catch (SagaConcurrencyException concurrencyError)
        {
            // A Task-returning API reports failures through the task rather than by throwing synchronously.
            return Task.FromException(concurrencyError);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TData>> GetStuckAsync(
        TimeSpan olderThan,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(olderThan, TimeSpan.Zero, nameof(olderThan));
        cancellationToken.ThrowIfCancellationRequested();

        var cutoff = DateTime.UtcNow - olderThan;
        IReadOnlyList<TData> stuck = _store
            .Values.Where(entry => entry.Data.UpdatedAtUtc <= cutoff)
            .OrderBy(entry => entry.Data.UpdatedAtUtc)
            .Select(entry => entry.Data)
            .ToArray();

        return Task.FromResult(stuck);
    }
}

public class SagaCoordinator<TData>
    where TData : class, ISagaData, new()
{
    private readonly ISagaRepository<TData> _repository;
    private readonly ILogger<SagaCoordinator<TData>>? _logger;
    private readonly SagaOptions _options;

    public SagaCoordinator(
        ISagaRepository<TData> repository,
        ILogger<SagaCoordinator<TData>>? logger = null,
        SagaOptions? options = null
    )
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
        _logger = logger;
        _options = options ?? new SagaOptions();
    }

    /// <summary>
    /// Creates a fluent saga builder for coordinating multi-step transactions with full backward compensation.
    /// </summary>
    public SagaExecutionBuilder<TData> Begin(Guid correlationId) => new(this, correlationId);

    public async Task<TData> ExecuteStepAsync<TEvent>(
        Guid correlationId,
        TEvent @event,
        ISagaStep<TData, TEvent> step,
        string nextState,
        CancellationToken cancellationToken = default
    )
    {
        var data =
            await _repository.FindByCorrelationIdAsync(correlationId, cancellationToken)
            ?? new TData { CorrelationId = correlationId, CreatedAtUtc = DateTime.UtcNow };

        try
        {
            _logger?.LogInformation(
                "Executing Saga step {Step} for CorrelationId: {CorrelationId}, CurrentState: {State}",
                step.Name,
                correlationId,
                data.CurrentState
            );

            await step.ExecuteAsync(data, @event, cancellationToken);
            data.CurrentState = nextState;
            await _repository.SaveAsync(data, cancellationToken);

            return data;
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                ex,
                "Saga step failed. Starting compensation for CorrelationId: {CorrelationId}",
                correlationId
            );

            // Use an independent timeout for compensation so that client timeout/cancellation does not abort rollback.
            using var compCts = new CancellationTokenSource(_options.CompensationTimeout);

            try
            {
                var compensatedSteps = data.CompensatedStepsOf();
                if (compensatedSteps.Contains(step.Name))
                {
                    _logger?.LogInformation(
                        "Saga step {Step} was already compensated for CorrelationId: {CorrelationId}; skipping duplicate compensation.",
                        step.Name,
                        correlationId
                    );
                }
                else
                {
                    await step.CompensateAsync(data, @event, compCts.Token);
                    compensatedSteps.Add(step.Name);
                }

                data.CurrentState = $"{nextState}_Compensated";
                await _repository.SaveAsync(data, compCts.Token);
            }
            catch (Exception compEx)
            {
                _logger?.LogCritical(
                    compEx,
                    "Compensation failed for CorrelationId: {CorrelationId}",
                    correlationId
                );
            }
            throw;
        }
    }

    /// <summary>
    /// Executes a sequence of steps. If any step fails, compensates all previously executed steps in reverse order (LIFO).
    /// </summary>
    public async Task<TData> ExecuteStepsAsync(
        Guid correlationId,
        IReadOnlyList<ISagaExecutableStep<TData>> steps,
        CancellationToken cancellationToken = default
    )
    {
        if (steps == null || steps.Count == 0)
        {
            throw new ArgumentException("Saga steps cannot be empty.", nameof(steps));
        }

        var data =
            await _repository.FindByCorrelationIdAsync(correlationId, cancellationToken)
            ?? new TData { CorrelationId = correlationId, CreatedAtUtc = DateTime.UtcNow };

        var executedSteps = new Stack<ISagaExecutableStep<TData>>();

        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            try
            {
                _logger?.LogInformation(
                    "Executing Saga step {Step} ({Index}/{Total}) for CorrelationId: {CorrelationId}, CurrentState: {State}",
                    step.Name,
                    i + 1,
                    steps.Count,
                    correlationId,
                    data.CurrentState
                );

                await step.ExecuteAsync(data, cancellationToken);
                data.CurrentState = step.NextState;
                await _repository.SaveAsync(data, cancellationToken);

                executedSteps.Push(step);
            }
            catch (Exception ex)
            {
                _logger?.LogError(
                    ex,
                    "Saga step {Step} failed for CorrelationId: {CorrelationId}. Starting backward compensation for {Count} executed steps.",
                    step.Name,
                    correlationId,
                    executedSteps.Count + 1
                );

                using var compCts = new CancellationTokenSource(_options.CompensationTimeout);

                // 1. Compensate the failed step itself (when its side effect already ran), then
                // 2. compensate the previously completed steps in reverse order (LIFO).
                // Both paths skip steps this saga already compensated, so a retried saga is safe.
                await CompensateSafelyAsync(step, data, compCts.Token);
                while (executedSteps.Count > 0)
                {
                    await CompensateSafelyAsync(executedSteps.Pop(), data, compCts.Token);
                }

                try
                {
                    data.CurrentState = $"{step.NextState}_Compensated";
                    await _repository.SaveAsync(data, compCts.Token);
                }
                catch (Exception saveEx)
                {
                    _logger?.LogCritical(
                        saveEx,
                        "Failed to persist compensated state for CorrelationId: {CorrelationId}",
                        correlationId
                    );
                }

                throw;
            }
        }

        return data;
    }

    /// <summary>
    /// Compensates one step at most once per correlation id: a retried saga that re-enters the failure path cannot undo
    /// the same side effect twice. A compensation that throws is not recorded, so a later retry attempts it again.
    /// </summary>
    private async Task CompensateSafelyAsync(
        ISagaExecutableStep<TData> step,
        TData data,
        CancellationToken cancellationToken
    )
    {
        var compensatedSteps = data.CompensatedStepsOf();
        if (compensatedSteps.Contains(step.Name))
        {
            _logger?.LogInformation(
                "Saga step {Step} was already compensated for CorrelationId: {CorrelationId}; skipping duplicate compensation.",
                step.Name,
                data.CorrelationId
            );
            return;
        }

        try
        {
            _logger?.LogInformation(
                "Compensating step {Step} for CorrelationId: {CorrelationId}",
                step.Name,
                data.CorrelationId
            );
            await step.CompensateAsync(data, cancellationToken);
            compensatedSteps.Add(step.Name);
        }
        catch (Exception compEx)
        {
            _logger?.LogCritical(
                compEx,
                "Compensation failed for step {Step}, CorrelationId: {CorrelationId}",
                step.Name,
                data.CorrelationId
            );
        }
    }
}

public sealed class SagaExecutionBuilder<TData>
    where TData : class, ISagaData, new()
{
    private readonly SagaCoordinator<TData> _coordinator;
    private readonly Guid _correlationId;
    private readonly List<ISagaExecutableStep<TData>> _steps = [];

    internal SagaExecutionBuilder(SagaCoordinator<TData> coordinator, Guid correlationId)
    {
        _coordinator = coordinator;
        _correlationId = correlationId;
    }

    public SagaExecutionBuilder<TData> Step<TEvent>(
        ISagaStep<TData, TEvent> step,
        TEvent @event,
        string nextState
    )
    {
        _steps.Add(new SagaExecutableStep<TData, TEvent>(step, @event, nextState));
        return this;
    }

    public Task<TData> ExecuteAsync(CancellationToken cancellationToken = default) =>
        _coordinator.ExecuteStepsAsync(_correlationId, _steps, cancellationToken);
}

/// <summary>
/// Behavioural knobs for <see cref="SagaCoordinator{TData}"/>, the in-memory repository fallback and the stuck-saga
/// monitor.
/// </summary>
public sealed class SagaOptions
{
    /// <summary>
    /// Volatile saga state is a development convenience, so <see cref="SagaServiceCollectionExtensions.AddLinKitSagas{TData}"/>
    /// refuses to fall back to <see cref="InMemorySagaRepository{TData}"/> in the Production environment unless this is
    /// set to <see langword="true"/>. Register a durable <see cref="ISagaRepository{TData}"/> instead.
    /// </summary>
    public bool AllowInMemoryInProduction { get; set; }

    /// <summary>Periodically report sagas that stopped advancing. Defaults to <see langword="true"/>.</summary>
    public bool EnableStuckSagaMonitor { get; set; } = true;

    /// <summary>A saga that has not advanced for this long is reported as stuck. Defaults to 15 minutes.</summary>
    public TimeSpan StuckAfter { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How often the stuck-saga monitor runs. Defaults to 5 minutes.</summary>
    public TimeSpan StuckSagaCheckInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long compensation may run. It is deliberately independent from the caller's token so that a client timeout
    /// cannot abort the rollback. Defaults to 30 seconds.
    /// </summary>
    public TimeSpan CompensationTimeout { get; set; } = TimeSpan.FromSeconds(30);

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            CompensationTimeout,
            TimeSpan.Zero,
            nameof(CompensationTimeout)
        );
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            StuckAfter,
            TimeSpan.Zero,
            nameof(StuckAfter)
        );
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            StuckSagaCheckInterval,
            TimeSpan.Zero,
            nameof(StuckSagaCheckInterval)
        );
    }
}

/// <summary>
/// Reports sagas that stopped advancing. A saga is left behind when a step throws after its side effect already ran or
/// when the process dies mid-step, so surfacing it is the only way an operator learns about it.
/// </summary>
internal sealed class SagaStuckMonitor<TData> : BackgroundService
    where TData : class, ISagaData
{
    private readonly ISagaRepository<TData> _repository;
    private readonly SagaOptions _options;
    private readonly ILogger<SagaStuckMonitor<TData>> _logger;

    public SagaStuckMonitor(
        ISagaRepository<TData> repository,
        SagaOptions options,
        ILogger<SagaStuckMonitor<TData>>? logger = null
    )
    {
        _repository = repository;
        _options = options;
        _logger = logger ?? NullLogger<SagaStuckMonitor<TData>>.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableStuckSagaMonitor)
        {
            return;
        }

        _logger.LogDebug(
            "Monitoring '{SagaType}' sagas for stuck instances older than {Threshold}.",
            typeof(TData).Name,
            _options.StuckAfter
        );

        while (!stoppingToken.IsCancellationRequested)
        {
            await ReportStuckSagasAsync(stoppingToken).ConfigureAwait(false);

            try
            {
                await Task.Delay(_options.StuckSagaCheckInterval, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ReportStuckSagasAsync(CancellationToken stoppingToken)
    {
        try
        {
            var stuck = await _repository
                .GetStuckAsync(_options.StuckAfter, stoppingToken)
                .ConfigureAwait(false);
            if (stuck.Count == 0)
            {
                return;
            }

            _logger.LogWarning(
                "{Count} '{SagaType}' saga(s) have not advanced for more than {Threshold}: {CorrelationIds}",
                stuck.Count,
                typeof(TData).Name,
                _options.StuckAfter,
                string.Join("; ", stuck.Select(saga => $"{saga.CorrelationId}@{saga.CurrentState}"))
            );
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is shutting down; nothing to report.
        }
        catch (Exception monitorError)
        {
            _logger.LogError(
                monitorError,
                "The stuck-saga check failed for '{SagaType}'.",
                typeof(TData).Name
            );
        }
    }
}

public static class SagaServiceCollectionExtensions
{
    /// <summary>
    /// Registers the saga coordinator, the stuck-saga monitor and the default in-memory repository.
    /// </summary>
    /// <remarks>
    /// <see cref="InMemorySagaRepository{TData}"/> is volatile and intended for development and testing. Register a
    /// durable <see cref="ISagaRepository{TData}"/> before calling this method. When none is registered the in-memory
    /// fallback is used, and it is rejected at resolution time when the host runs in Production unless
    /// <see cref="SagaOptions.AllowInMemoryInProduction"/> was enabled.
    /// </remarks>
    public static IServiceCollection AddLinKitSagas<TData>(
        this IServiceCollection services,
        Action<SagaOptions>? configure = null
    )
        where TData : class, ISagaData, new()
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new SagaOptions();
        configure?.Invoke(options);
        options.Validate();

        services.TryAddSingleton(options);
        services.TryAddSingleton(sp => CreateRepository<TData>(sp));
        services.TryAddScoped<SagaCoordinator<TData>>();
        services.AddHostedService<SagaStuckMonitor<TData>>();
        return services;
    }

    private static ISagaRepository<TData> CreateRepository<TData>(IServiceProvider services)
        where TData : class, ISagaData, new()
    {
        var options = services.GetService<SagaOptions>() ?? new SagaOptions();
        var environment = services.GetService<IHostEnvironment>();

        if (environment?.IsProduction() == true && !options.AllowInMemoryInProduction)
        {
            throw new InvalidOperationException(
                $"No durable {nameof(ISagaRepository<TData>)} is registered for saga '{typeof(TData).Name}'. The in-memory "
                    + "fallback keeps state in process memory, so it is rejected when the host runs in Production: register a "
                    + $"durable {nameof(ISagaRepository<TData>)} before AddLinKitSagas<{typeof(TData).Name}>(), or set "
                    + $"{nameof(SagaOptions)}.{nameof(SagaOptions.AllowInMemoryInProduction)} = true to accept volatile saga state."
            );
        }

        return new InMemorySagaRepository<TData>();
    }
}
