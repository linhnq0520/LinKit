namespace LinKit.Core.BackgroundJobs;

public interface IBackgroundJobTrigger
{
    Task TriggerAsync(string jobName, CancellationToken cancellationToken = default);

    Task TriggerAsync(
        string jobName,
        string? embeddedData,
        CancellationToken cancellationToken = default
    );
}
