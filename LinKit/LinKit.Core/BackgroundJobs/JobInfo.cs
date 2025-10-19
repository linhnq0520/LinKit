namespace LinKit.Core.BackgroundJobs;

public class JobInfo
{
    public Type? JobType { get; set; }
    public bool IsCommand { get; set; }
    public object? Instance { get; set; }
    public bool HasResult { get; set; }

    public JobInfo() { }

    public JobInfo(
        Type? jobType,
        bool isCommand,
        object? instance = default,
        bool hasResult = false
    )
    {
        JobType = jobType;
        IsCommand = isCommand;
        Instance = instance;
        HasResult = hasResult;
    }
}
