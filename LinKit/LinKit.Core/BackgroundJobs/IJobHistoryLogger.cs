namespace LinKit.Core.BackgroundJobs;

/// <summary>
/// Persists the outcome of a job execution. Register an implementation to keep history in a database, file or log
/// sink; when none is registered, history records are discarded.
/// </summary>
public interface IJobHistoryLogger
{
    /// <summary>
    /// Stores one execution record.
    /// </summary>
    /// <param name="history">The record to store.</param>
    /// <param name="cancellationToken">
    /// Token that bounds the write. It is a short timeout created by the runner rather than the job token, so history is
    /// still written while the host is shutting down.
    /// </param>
    /// <returns>A task that completes when the record has been persisted.</returns>
    Task LogAsync(JobExecutionHistory history, CancellationToken cancellationToken = default);
}
