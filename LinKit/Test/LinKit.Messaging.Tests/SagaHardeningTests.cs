using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LinKit.Sagas;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LinKit.Messaging.Tests;

/// <summary>
/// Covers the Phase 1.9 saga contract: optimistic concurrency, idempotent compensation on retry, stuck-saga discovery,
/// and the production guard on the in-memory repository.
/// </summary>
public sealed class SagaHardeningTests
{
    [Fact]
    public async Task ASaveAdvancesTheVersionAndRejectsTheStaleWriter()
    {
        var repository = new InMemorySagaRepository<TestSagaData>();
        var correlationId = Guid.NewGuid();
        var state = new TestSagaData { CorrelationId = correlationId };

        await repository.SaveAsync(state);
        Assert.Equal(1, state.Version);

        state.CurrentState = "StockReserved";
        await repository.SaveAsync(state);
        Assert.Equal(2, state.Version);

        // A worker that loaded version 1 before the update must not be able to overwrite the newer state.
        var staleWriter = new TestSagaData { CorrelationId = correlationId, Version = 1 };

        var conflict = await Assert.ThrowsAsync<SagaConcurrencyException>(() =>
            repository.SaveAsync(staleWriter)
        );

        Assert.Equal(correlationId, conflict.CorrelationId);
        Assert.Equal(1, conflict.ExpectedVersion);
        Assert.Equal(2, conflict.ActualVersion);
    }

    [Fact]
    public async Task TwoWorkersCannotBothAdvanceTheSameSaga()
    {
        var repository = new InMemorySagaRepository<TestSagaData>();
        var correlationId = Guid.NewGuid();
        await repository.SaveAsync(new TestSagaData { CorrelationId = correlationId });

        var firstWorker = new TestSagaData { CorrelationId = correlationId, Version = 1 };
        var secondWorker = new TestSagaData { CorrelationId = correlationId, Version = 1 };

        await repository.SaveAsync(firstWorker);

        await Assert.ThrowsAsync<SagaConcurrencyException>(() =>
            repository.SaveAsync(secondWorker)
        );
    }

    [Fact]
    public async Task ARetryDoesNotCompensateTheSameStepTwice()
    {
        var repository = new InMemorySagaRepository<TestSagaData>();
        var correlationId = Guid.NewGuid();
        var coordinator = new SagaCoordinator<TestSagaData>(repository);

        var step1 = new SuccessfulStep("Step1");
        var step2 = new FailingStep("Step2");

        // The same saga is delivered twice, as an at-least-once transport would do after the first compensation.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                coordinator
                    .Begin(correlationId)
                    .Step(step1, "event1", "Step1_Done")
                    .Step(step2, "event2", "Step2_Done")
                    .ExecuteAsync()
            );
        }

        var state = await repository.FindByCorrelationIdAsync(correlationId);

        Assert.NotNull(state);
        Assert.Equal("Step2_Done_Compensated", state.CurrentState);
        Assert.Equal(["Step2", "Step1"], state.CompensatedLog);
        Assert.Equal(2, state.CompensatedSteps.Count);
        Assert.Contains("Step1", state.CompensatedSteps);
        Assert.Contains("Step2", state.CompensatedSteps);
    }

    [Fact]
    public async Task StepsAlreadyCompensatedByEarlierDeliveryAreSkipped()
    {
        var repository = new InMemorySagaRepository<TestSagaData>();
        var correlationId = Guid.NewGuid();
        var seeded = new TestSagaData { CorrelationId = correlationId };
        seeded.CompensatedSteps.Add("Step1");
        await repository.SaveAsync(seeded);

        var coordinator = new SagaCoordinator<TestSagaData>(repository);
        var step1 = new SuccessfulStep("Step1");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator
                .Begin(correlationId)
                .Step(step1, "event1", "Step1_Done")
                .Step(new FailingStep("Step2"), "event2", "Step2_Done")
                .ExecuteAsync()
        );

        var state = await repository.FindByCorrelationIdAsync(correlationId);

        Assert.NotNull(state);
        // Step 1 was compensated before, so only the failed Step 2 is undone by this delivery.
        Assert.Equal(["Step2"], state.CompensatedLog);
    }

    [Fact]
    public async Task GetStuckAsyncReturnsOnlySagasThatStoppedAdvancing()
    {
        var repository = new InMemorySagaRepository<TestSagaData>();
        var stuckId = Guid.NewGuid();
        var freshId = Guid.NewGuid();

        var stuck = new TestSagaData { CorrelationId = stuckId, CurrentState = "AwaitingPayment" };
        await repository.SaveAsync(stuck);
        stuck.UpdatedAtUtc = DateTime.UtcNow - TimeSpan.FromMinutes(10);

        await repository.SaveAsync(new TestSagaData { CorrelationId = freshId });

        var found = await repository.GetStuckAsync(TimeSpan.FromMinutes(5));

        var reported = Assert.Single(found);
        Assert.Equal(stuckId, reported.CorrelationId);
        Assert.Equal("AwaitingPayment", reported.CurrentState);
    }

    [Fact]
    public async Task GetStuckAsyncRejectsANegativeThreshold()
    {
        var repository = new InMemorySagaRepository<TestSagaData>();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            repository.GetStuckAsync(TimeSpan.FromTicks(-1))
        );
    }

    [Fact]
    public async Task TheInMemoryRepositoryIsRejectedInProduction()
    {
        await using var provider = new ServiceCollection()
            .AddSingleton<IHostEnvironment>(new FakeHostEnvironment(Environments.Production))
            .AddLinKitSagas<TestSagaData>()
            .BuildServiceProvider();

        var error = Assert.Throws<InvalidOperationException>(() =>
            provider.GetRequiredService<ISagaRepository<TestSagaData>>()
        );

        Assert.Contains("Production", error.Message);
        Assert.Contains(nameof(SagaOptions.AllowInMemoryInProduction), error.Message);
    }

    [Fact]
    public async Task TheInMemoryRepositoryIsAllowedInProductionWhenExplicitlyOptedIn()
    {
        await using var provider = new ServiceCollection()
            .AddSingleton<IHostEnvironment>(new FakeHostEnvironment(Environments.Production))
            .AddLinKitSagas<TestSagaData>(options => options.AllowInMemoryInProduction = true)
            .BuildServiceProvider();

        Assert.IsType<InMemorySagaRepository<TestSagaData>>(
            provider.GetRequiredService<ISagaRepository<TestSagaData>>()
        );

        // The whole graph (including the stuck-saga monitor) must be constructible without an ILogger registration.
        Assert.NotNull(provider.GetRequiredService<SagaCoordinator<TestSagaData>>());
    }

    [Fact]
    public async Task ADurableRepositoryRegisteredFirstWins()
    {
        var durable = new RecordingRepository();

        await using var provider = new ServiceCollection()
            .AddSingleton<IHostEnvironment>(new FakeHostEnvironment(Environments.Production))
            .AddSingleton<ISagaRepository<TestSagaData>>(durable)
            .AddLinKitSagas<TestSagaData>()
            .BuildServiceProvider();

        Assert.Same(durable, provider.GetRequiredService<ISagaRepository<TestSagaData>>());
    }

    [Fact]
    public void InvalidOptionsAreRejected()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            services.AddLinKitSagas<TestSagaData>(options =>
                options.CompensationTimeout = TimeSpan.Zero
            )
        );
    }

    private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "LinKit.Messaging.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class RecordingRepository : ISagaRepository<TestSagaData>
    {
        private readonly Dictionary<Guid, TestSagaData> _states = [];

        public Task<TestSagaData?> FindByCorrelationIdAsync(
            Guid correlationId,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(_states.TryGetValue(correlationId, out var state) ? state : null);

        public Task SaveAsync(TestSagaData data, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(data);
            _states[data.CorrelationId] = data;
            data.Version++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TestSagaData>> GetStuckAsync(
            TimeSpan olderThan,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlyList<TestSagaData>>([]);
    }
}
