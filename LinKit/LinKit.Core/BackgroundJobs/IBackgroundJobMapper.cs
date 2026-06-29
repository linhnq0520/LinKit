using LinKit.Core.Cqrs;

namespace LinKit.Core.BackgroundJobs;

public interface IBackgroundJobMapper
{
    JobInfo GetJobInfoByName(string jobName);

    Func<IMediator, CancellationToken, Task>? GetExecutor(
        string jobName,
        string embeddedData,
        string executionId
    );
}
