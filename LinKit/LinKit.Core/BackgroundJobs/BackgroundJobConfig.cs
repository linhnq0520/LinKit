namespace LinKit.Core.BackgroundJobs;

/// <summary>
/// Root object deserialized from the background-job JSON configuration file.
/// Each entry in <see cref="BackgroundJobs"/> describes one job: which type to run, when to run it
/// and how many instances may run in parallel.
/// </summary>
public class BackgroundJobConfig
{
    /// <summary>
    /// Optional connection string used by the job history store. When it is <see langword="null"/> or empty,
    /// history is written to the in-memory store and is lost on restart.
    /// </summary>
    public string? HistoryConnectionString { get; set; }

    /// <summary>
    /// Jobs to register with the scheduler. The list may be empty, in which case the manager starts but does nothing.
    /// </summary>
    public List<JobConfig> BackgroundJobs { get; set; } = [];
}

/// <summary>
/// Describes a single background job: its identity, enablement, schedule and execution limits.
/// Two instances are equal when every scheduling and execution property matches, which allows the manager to
/// detect configuration changes between reloads.
/// </summary>
public class JobConfig : IEquatable<JobConfig>
{
    /// <summary>
    /// Unique job name. It is used as the dictionary key and as the history correlation key,
    /// so two jobs must never share the same name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Whether the job is scheduled. When <see langword="false"/>, the job is registered but never triggered.
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// Whether each execution writes a history record. Disable it for high-frequency jobs to avoid unbounded growth.
    /// </summary>
    public bool IsLogHistory { get; set; }

    /// <summary>
    /// Operating-system time zone identifier, for example <c>"SE Asia Standard Time"</c> on Windows or
    /// <c>"Asia/Ho_Chi_Minh"</c> on Linux and macOS. Prefer this over <see cref="UtcOffsetHours"/> because only a full
    /// time zone can apply daylight-saving transitions. An unknown identifier falls back to UTC.
    /// </summary>
    public string? TimeZoneId { get; set; }

    /// <summary>
    /// Fixed UTC offset in hours, for example <c>7</c>, <c>-7</c> or <c>5.5</c>.
    /// Only used when <see cref="TimeZoneId"/> is not set; values outside the range -14 to +14 fall back to UTC.
    /// A fixed offset never observes daylight saving time.
    /// </summary>
    public double? UtcOffsetHours { get; set; }

    #region Scheduling Configuration

    /// <summary>
    /// Schedule shape for this job: <see cref="ScheduleType.Interval"/>, <see cref="ScheduleType.Daily"/>,
    /// <see cref="ScheduleType.Weekly"/> or <see cref="ScheduleType.Monthly"/>. Defaults to
    /// <see cref="ScheduleType.Interval"/>.
    /// </summary>
    public ScheduleType ScheduleType { get; set; } = ScheduleType.Interval;

    /// <summary>
    /// Interval between two runs in seconds. Only used when <see cref="ScheduleType"/> is
    /// <see cref="ScheduleType.Interval"/>. Defaults to 60 seconds.
    /// </summary>
    public double TimeIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Time of day to run, formatted as <c>"HH:mm:ss"</c>, for example <c>"04:30:00"</c>.
    /// Used by <see cref="ScheduleType.Daily"/>, <see cref="ScheduleType.Weekly"/> and
    /// <see cref="ScheduleType.Monthly"/>; an invalid value falls back to midnight.
    /// </summary>
    public string? TimeOfDay { get; set; }

    /// <summary>
    /// Day of the week to run. Used only by <see cref="ScheduleType.Weekly"/>;
    /// when it is <see langword="null"/> the job runs every day at <see cref="TimeOfDay"/>.
    /// </summary>
    public DayOfWeek? DayOfWeek { get; set; }

    /// <summary>
    /// Day of the month to run, from 1 to 31. Used only by <see cref="ScheduleType.Monthly"/>.
    /// Use <c>-1</c> to target the last day of every month; any value greater than the length of the current month is
    /// clamped to that month's last day, so <c>31</c> also targets the last day in February, April, June, September and
    /// November.
    /// </summary>
    public int? DayOfMonth { get; set; }

    /// <summary>
    /// When <see langword="true"/>, the job runs once as soon as the manager starts and then continues on its schedule.
    /// </summary>
    public bool RunOnStart { get; set; } = false;

    #endregion

    #region Execution Configuration

    /// <summary>
    /// Maximum number of concurrent executions allowed for this job. Values below 1 are treated as 1.
    /// </summary>
    public int MaxParallel { get; set; } = 1;

    /// <summary>
    /// Name of the assembly that defines the job type. Used to resolve the implementation at runtime.
    /// </summary>
    public string AssemblyName { get; set; } = string.Empty;

    /// <summary>
    /// JSON payload handed to the job instance, allowing the same job type to be scheduled several times with
    /// different arguments. The payload is deserialized with the application's configured JSON options.
    /// </summary>
    public string EmbeddedData { get; set; } = string.Empty;

    #endregion

    #region Equality Implementation

    /// <summary>
    /// Determines whether this configuration matches <paramref name="other"/> on every scheduling and execution
    /// property, ignoring reference identity.
    /// </summary>
    /// <param name="other">The configuration to compare with.</param>
    /// <returns><see langword="true"/> when all properties are equal; otherwise <see langword="false"/>.</returns>
    public bool Equals(JobConfig? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return Name == other.Name
            && IsActive == other.IsActive
            && IsLogHistory == other.IsLogHistory
            && TimeZoneId == other.TimeZoneId
            && UtcOffsetHours == other.UtcOffsetHours
            && ScheduleType == other.ScheduleType
            && TimeIntervalSeconds == other.TimeIntervalSeconds
            && TimeOfDay == other.TimeOfDay
            && DayOfWeek == other.DayOfWeek
            && DayOfMonth == other.DayOfMonth
            && RunOnStart == other.RunOnStart
            && MaxParallel == other.MaxParallel
            && AssemblyName == other.AssemblyName
            && EmbeddedData == other.EmbeddedData;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return Equals(obj as JobConfig);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hashCode = new HashCode();
        hashCode.Add(Name);
        hashCode.Add(IsActive);
        hashCode.Add(IsLogHistory);
        hashCode.Add(TimeZoneId);
        hashCode.Add(UtcOffsetHours);
        hashCode.Add(ScheduleType);
        hashCode.Add(TimeIntervalSeconds);
        hashCode.Add(TimeOfDay);
        hashCode.Add(DayOfWeek);
        hashCode.Add(DayOfMonth);
        hashCode.Add(RunOnStart);
        hashCode.Add(MaxParallel);
        hashCode.Add(AssemblyName);
        hashCode.Add(EmbeddedData);
        return hashCode.ToHashCode();
    }

    #endregion
}

/// <summary>
/// Schedule shapes supported by <see cref="Scheduler"/>.
/// </summary>
public enum ScheduleType
{
    /// <summary>
    /// Runs repeatedly with a fixed delay between runs, configured by <see cref="JobConfig.TimeIntervalSeconds"/>.
    /// </summary>
    Interval,

    /// <summary>
    /// Runs once per day at <see cref="JobConfig.TimeOfDay"/>.
    /// </summary>
    Daily,

    /// <summary>
    /// Runs once per week on <see cref="JobConfig.DayOfWeek"/> at <see cref="JobConfig.TimeOfDay"/>.
    /// </summary>
    Weekly,

    /// <summary>
    /// Runs once per month on <see cref="JobConfig.DayOfMonth"/> at <see cref="JobConfig.TimeOfDay"/>.
    /// </summary>
    Monthly,
}
