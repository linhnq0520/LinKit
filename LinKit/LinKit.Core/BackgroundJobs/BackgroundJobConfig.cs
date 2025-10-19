namespace LinKit.Core.BackgroundJobs;

public class BackgroundJobConfig
{
    public List<JobConfig> BackgroundJobs { get; set; } = new();
}

public class JobConfig
{
    public string Name { get; set; } = "";
    public int TimeIntervalSeconds { get; set; } = 10;
    public int MaxParallel { get; set; } = 1;
    public string? CorrelationId { get; set; }
    public bool IsActive { get; set; } = true;
    public string? EmbeddedData { get; set; }

    public override bool Equals(object? obj)
    {
        if (obj is not JobConfig other)
            return false;
        return Name == other.Name && MaxParallel == other.MaxParallel;
    }

    public override int GetHashCode() => HashCode.Combine(Name, MaxParallel);
}
