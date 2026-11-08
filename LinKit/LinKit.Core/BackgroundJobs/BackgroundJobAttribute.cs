namespace LinKit.Core.BackgroundJobs;

/// <summary>
/// Declares the schedulable name of a job type. The name is what <see cref="JobConfig.Name"/> refers to, so it can differ
/// from the class name and stay stable when the type is renamed.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class BackgroundJobAttribute : Attribute
{
    /// <summary>
    /// Gets the name used in the configuration file and in job history.
    /// </summary>
    public string JobName { get; }

    /// <summary>
    /// Initializes the attribute.
    /// </summary>
    /// <param name="jobName">Name used in the configuration file and in job history.</param>
    public BackgroundJobAttribute(string jobName)
    {
        JobName = jobName;
    }
}
