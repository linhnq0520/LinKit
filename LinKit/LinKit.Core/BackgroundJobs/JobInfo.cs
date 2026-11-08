using LinKit.Core.Cqrs;

namespace LinKit.Core.BackgroundJobs;

/// <summary>
/// Runtime description of a job that the generated code can execute: either a command dispatched through the mediator or a
/// plain instance whose method is invoked directly.
/// </summary>
public class JobInfo
{
    /// <summary>
    /// Gets or sets the CLR type that implements the job.
    /// </summary>
    public Type? JobType { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the job is an <see cref="ICommand"/> dispatched through the mediator
    /// instead of an object invoked directly.
    /// </summary>
    public bool IsCommand { get; set; }

    /// <summary>
    /// Gets or sets the job instance, already populated with the data from the configuration.
    /// </summary>
    public object? Instance { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the job returns a value. A job with a result is still dispatched through
    /// the mediator; the result is ignored by the scheduler.
    /// </summary>
    public bool HasResult { get; set; }

    /// <summary>
    /// Gets or sets the delegate that performs the execution with a mediator and cancellation token, generated per job
    /// type so no reflection is used at runtime.
    /// </summary>
    public Func<IMediator, object, CancellationToken, Task>? Executor { get; set; }

    /// <summary>
    /// Initializes an empty instance for object initializer syntax.
    /// </summary>
    public JobInfo() { }

    /// <summary>
    /// Initializes a fully described job.
    /// </summary>
    /// <param name="jobType">CLR type that implements the job.</param>
    /// <param name="isCommand">Whether the job is dispatched through the mediator.</param>
    /// <param name="instance">Job instance to execute.</param>
    /// <param name="executor">Delegate that performs the execution.</param>
    public JobInfo(
        Type jobType,
        bool isCommand,
        object instance,
        Func<IMediator, object, CancellationToken, Task> executor
    )
    {
        JobType = jobType;
        IsCommand = isCommand;
        Instance = instance;
        Executor = executor;
    }
}
