using LinKit.Core.Cqrs;

namespace LinKit.Core.BackgroundJobs;

/// <summary>
/// Bridges configuration names to executable job delegates. The LinKit source generator emits one implementation per
/// assembly and registers it as a keyed service, which is how jobs are resolved without reflection.
/// </summary>
public interface IBackgroundJobMapper
{
    /// <summary>
    /// Returns the metadata of a job by name.
    /// </summary>
    /// <param name="jobName">Name declared by <see cref="BackgroundJobAttribute.JobName"/> or the configuration.</param>
    /// <returns>The job description, or a description with no executor when the name is unknown.</returns>
    JobInfo GetJobInfoByName(string jobName);

    /// <summary>
    /// Returns a delegate that deserializes <paramref name="embeddedData"/> into the job's parameters and executes the job.
    /// </summary>
    /// <param name="jobName">Name of the job.</param>
    /// <param name="embeddedData">JSON payload for this execution.</param>
    /// <param name="executionId">Identifier of this execution, exposed to the job through the command.</param>
    /// <returns>The executor, or <see langword="null"/> when no job with that name is generated in this assembly.</returns>
    Func<IMediator, CancellationToken, Task>? GetExecutor(
        string jobName,
        string embeddedData,
        string executionId
    );
}
