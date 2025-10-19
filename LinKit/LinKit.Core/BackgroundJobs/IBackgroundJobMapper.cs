namespace LinKit.Core.BackgroundJobs;

public interface IBackgroundJobMapper
{
    public JobInfo GetJobInfoByName(string jobName);
}
