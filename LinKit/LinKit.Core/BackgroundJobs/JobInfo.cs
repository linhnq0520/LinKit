using LinKit.Core.Cqrs;

namespace LinKit.Core.BackgroundJobs;

public class JobInfo
{
    public Type? JobType { get; set; }
    public bool IsCommand { get; set; }
    public object? Instance { get; set; }
    public bool HasResult { get; set; }
    public Func<IMediator, object, CancellationToken, Task>? Executor { get; set; }

    public JobInfo() { }

    public JobInfo(Type jobType, bool isCommand, object instance,
        Func<IMediator, object, CancellationToken, Task> executor)
    {
        JobType = jobType;
        IsCommand = isCommand;
        Instance = instance;
        Executor = executor;
    }
}
