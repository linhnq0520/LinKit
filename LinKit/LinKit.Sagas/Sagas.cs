using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace LinKit.Sagas;

public interface ISagaData
{
    Guid CorrelationId { get; set; }
    string CurrentState { get; set; }
    DateTime CreatedAtUtc { get; set; }
    DateTime UpdatedAtUtc { get; set; }
}

public class SagaData : ISagaData
{
    public Guid CorrelationId { get; set; } = Guid.NewGuid();
    public string CurrentState { get; set; } = "Initial";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public interface ISagaStep<TData, in TEvent> where TData : ISagaData
{
    string Name => GetType().Name;

    Task ExecuteAsync(TData data, TEvent @event, CancellationToken cancellationToken = default);
    Task CompensateAsync(TData data, TEvent @event, CancellationToken cancellationToken = default);
}

public interface ISagaRepository<TData> where TData : ISagaData
{
    Task<TData?> FindByCorrelationIdAsync(Guid correlationId, CancellationToken cancellationToken = default);
    Task SaveAsync(TData data, CancellationToken cancellationToken = default);
}

public class InMemorySagaRepository<TData> : ISagaRepository<TData> where TData : class, ISagaData
{
    private readonly ConcurrentDictionary<Guid, TData> _store = new();

    public Task<TData?> FindByCorrelationIdAsync(Guid correlationId, CancellationToken cancellationToken = default)
    {
        _store.TryGetValue(correlationId, out var data);
        return Task.FromResult(data);
    }

    public Task SaveAsync(TData data, CancellationToken cancellationToken = default)
    {
        data.UpdatedAtUtc = DateTime.UtcNow;
        _store[data.CorrelationId] = data;
        return Task.CompletedTask;
    }
}

public class SagaCoordinator<TData> where TData : class, ISagaData, new()
{
    private readonly ISagaRepository<TData> _repository;
    private readonly ILogger<SagaCoordinator<TData>>? _logger;

    public SagaCoordinator(ISagaRepository<TData> repository, ILogger<SagaCoordinator<TData>>? logger = null)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<TData> ExecuteStepAsync<TEvent>(
        Guid correlationId,
        TEvent @event,
        ISagaStep<TData, TEvent> step,
        string nextState,
        CancellationToken cancellationToken = default)
    {
        var data = await _repository.FindByCorrelationIdAsync(correlationId, cancellationToken) ?? new TData
        {
            CorrelationId = correlationId,
            CreatedAtUtc = DateTime.UtcNow
        };

        try
        {
            _logger?.LogInformation("Executing Saga step {Step} for CorrelationId: {CorrelationId}, CurrentState: {State}",
                step.Name, correlationId, data.CurrentState);

            await step.ExecuteAsync(data, @event, cancellationToken);
            data.CurrentState = nextState;
            await _repository.SaveAsync(data, cancellationToken);

            return data;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Saga step failed. Starting compensation for CorrelationId: {CorrelationId}", correlationId);
            try
            {
                // Use an independent timeout for compensation so that client timeout/cancellation does not abort rollback
                using var compCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await step.CompensateAsync(data, @event, compCts.Token);
                data.CurrentState = $"{nextState}_Compensated";
                await _repository.SaveAsync(data, compCts.Token);
            }
            catch (Exception compEx)
            {
                _logger?.LogCritical(compEx, "Compensation failed for CorrelationId: {CorrelationId}", correlationId);
            }
            throw;
        }
    }
}

public static class SagaServiceCollectionExtensions
{
    /// <summary>
    /// Registers LinKit Saga coordinator and default in-memory repository.
    /// </summary>
    /// <remarks>
    /// WARNING: <see cref="InMemorySagaRepository{TData}"/> is volatile and intended for development/testing.
    /// For production use, register a durable <see cref="ISagaRepository{TData}"/> prior to calling this method.
    /// </remarks>
    public static IServiceCollection AddLinKitSagas<TData>(this IServiceCollection services)
        where TData : class, ISagaData, new()
    {
        services.TryAddSingleton<ISagaRepository<TData>, InMemorySagaRepository<TData>>();
        services.AddScoped<SagaCoordinator<TData>>();
        return services;
    }
}
