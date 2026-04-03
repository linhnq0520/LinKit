namespace LinKit.Core.BackgroundJobs;

public interface IJobHistoryLogger
{
    Task LogAsync(JobExecutionHistory history, CancellationToken cancellationToken = default);
}
