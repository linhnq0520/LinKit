using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LinKit.Sagas;
using Xunit;

namespace LinKit.Messaging.Tests;

public class TestSagaData : SagaData
{
    public List<string> ExecutedLog { get; set; } = [];
    public List<string> CompensatedLog { get; set; } = [];
}

public class SuccessfulStep(string name) : ISagaStep<TestSagaData, string>
{
    public string Name => name;

    public Task ExecuteAsync(TestSagaData data, string @event, CancellationToken cancellationToken = default)
    {
        data.ExecutedLog.Add(Name);
        return Task.CompletedTask;
    }

    public Task CompensateAsync(TestSagaData data, string @event, CancellationToken cancellationToken = default)
    {
        data.CompensatedLog.Add(Name);
        return Task.CompletedTask;
    }
}

public class FailingStep(string name) : ISagaStep<TestSagaData, string>
{
    public string Name => name;

    public Task ExecuteAsync(TestSagaData data, string @event, CancellationToken cancellationToken = default)
    {
        data.ExecutedLog.Add(Name);
        throw new InvalidOperationException($"Step {Name} failed intentionally.");
    }

    public Task CompensateAsync(TestSagaData data, string @event, CancellationToken cancellationToken = default)
    {
        data.CompensatedLog.Add(Name);
        return Task.CompletedTask;
    }
}

public class SagaCoordinatorTests
{
    [Fact]
    public async Task FluentSaga_WhenAllStepsSucceed_CompletesSuccessfully()
    {
        var repo = new InMemorySagaRepository<TestSagaData>();
        var coordinator = new SagaCoordinator<TestSagaData>(repo);
        var correlationId = Guid.NewGuid();

        var state = await coordinator.Begin(correlationId)
            .Step(new SuccessfulStep("Step1"), "event1", "Step1_Done")
            .Step(new SuccessfulStep("Step2"), "event2", "Step2_Done")
            .Step(new SuccessfulStep("Step3"), "event3", "Completed")
            .ExecuteAsync();

        Assert.Equal("Completed", state.CurrentState);
        Assert.Equal(["Step1", "Step2", "Step3"], state.ExecutedLog);
        Assert.Empty(state.CompensatedLog);
    }

    [Fact]
    public async Task FluentSaga_WhenStep2Fails_CompensatesPreviousStepsInReverseOrder()
    {
        var repo = new InMemorySagaRepository<TestSagaData>();
        var coordinator = new SagaCoordinator<TestSagaData>(repo);
        var correlationId = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await coordinator.Begin(correlationId)
                .Step(new SuccessfulStep("Step1"), "event1", "Step1_Done")
                .Step(new FailingStep("Step2"), "event2", "Step2_Done")
                .Step(new SuccessfulStep("Step3"), "event3", "Completed")
                .ExecuteAsync();
        });

        var state = await repo.FindByCorrelationIdAsync(correlationId);
        Assert.NotNull(state);
        Assert.Equal("Step2_Done_Compensated", state.CurrentState);
        Assert.Equal(["Step1", "Step2"], state.ExecutedLog);
        // Step 2 failed -> compensated, then Step 1 compensated (LIFO order)
        Assert.Equal(["Step2", "Step1"], state.CompensatedLog);
    }
}

