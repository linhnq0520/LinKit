using LinKit.Core.BackgroundJobs;
using LinKit.Core.Cqrs;

namespace SampleWebApp.Features.BackgroundJobs;

[BackgroundJob("TestBackgroundJob")]
public class TestBackgroundJob : BackgroundJobCommand
{
}

[CqrsHandler]
public class TestBackgroundJobHandler : ICommandHandler<TestBackgroundJob>
{
    public async Task<Unit> HandleAsync(TestBackgroundJob request, CancellationToken cancellationToken = default)
    {
        Console.WriteLine("Test job");
        return Unit.Value;
    }
}
