namespace LinKit.Core.BackgroundJobs;

/// <summary>
/// Triggers a configured background job on demand, bypassing its schedule. Resolved from the container and implemented
/// by <see cref="BackgroundJobManager"/>.
/// </summary>
public interface IBackgroundJobTrigger
{
    /// <summary>
    /// Runs the job immediately with the payload from its configuration.
    /// </summary>
    /// <param name="jobName">Name of the job, matched case-insensitively.</param>
    /// <param name="cancellationToken">Token that cancels the run.</param>
    /// <returns>A task that completes when the run finishes.</returns>
    /// <exception cref="ArgumentException"><paramref name="jobName"/> is <see langword="null"/> or blank.</exception>
    /// <exception cref="InvalidOperationException">No job with that name exists in the configuration.</exception>
    Task TriggerAsync(string jobName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the job immediately with an explicit payload instead of the one from its configuration.
    /// </summary>
    /// <param name="jobName">Name of the job, matched case-insensitively.</param>
    /// <param name="embeddedData">
    /// JSON payload for this run, or <see langword="null"/> to use <see cref="JobConfig.EmbeddedData"/>.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the run.</param>
    /// <returns>A task that completes when the run finishes.</returns>
    /// <exception cref="ArgumentException"><paramref name="jobName"/> is <see langword="null"/> or blank.</exception>
    /// <exception cref="InvalidOperationException">No job with that name exists in the configuration.</exception>
    Task TriggerAsync(
        string jobName,
        string? embeddedData,
        CancellationToken cancellationToken = default
    );
}
