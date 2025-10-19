namespace LinKit.Core.BackgroundJobs;

[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class BackgroundJobAttribute : Attribute
{
    public string JobName { get; }

    public BackgroundJobAttribute(string jobName)
    {
        JobName = jobName;
    }
}
