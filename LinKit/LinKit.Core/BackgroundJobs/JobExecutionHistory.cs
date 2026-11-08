namespace LinKit.Core.BackgroundJobs;

/// <summary>
/// One record of a job execution, written by the runner when <see cref="JobConfig.IsLogHistory"/> is
/// <see langword="true"/> and an <see cref="IJobHistoryLogger"/> is registered.
/// </summary>
public class JobExecutionHistory
{
    /// <summary>
    /// Gets or sets the identifier of this execution. Use it to correlate logs and to locate a retry.
    /// </summary>
    public string ExecutionId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the configured name of the job that ran.
    /// </summary>
    public string JobName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the UTC start time of the execution.
    /// </summary>
    public DateTime StartTime { get; set; }

    /// <summary>
    /// Gets or sets the UTC end time, or <see langword="null"/> while the job is still running.
    /// </summary>
    public DateTime? EndTime { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the job completed without throwing.
    /// </summary>
    public bool IsSuccess { get; set; }

    /// <summary>
    /// Gets or sets the full exception text when the job failed, otherwise <see langword="null"/>.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Gets or sets the JSON payload that was passed to the job, which makes a failure reproducible.
    /// </summary>
    public string? EmbeddedData { get; set; }
}
