using LinKit.Core.Cqrs;

namespace LinKit.Core.BackgroundJobs;

/// <summary>
/// Base type for a job that runs through the CQRS pipeline, so behaviors such as logging, validation and transactions
/// apply to background work exactly as they do to HTTP requests.
/// </summary>
public abstract class BackgroundJobCommand : ICommand
{
    /// <summary>
    /// Gets or sets the JSON payload supplied by the job configuration, already deserialized into the command.
    /// </summary>
    public string? EmbeddedData { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the current execution, which lets the handler correlate its own logs with the job
    /// history record.
    /// </summary>
    public string? ExecutionId { get; set; }
}
