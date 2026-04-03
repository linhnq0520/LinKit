namespace LinKit.Core.BackgroundJobs;

public class BackgroundJobConfig
{
    public string? HistoryConnectionString { get; set; }
    public List<JobConfig> BackgroundJobs { get; set; } = [];
}

public class JobConfig : IEquatable<JobConfig>
{
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool IsLogHistory { get; set; }

    /// <summary>
    /// ID timezone hệ điều hành (ví dụ: "SE Asia Standard Time", "Asia/Ho_Chi_Minh").
    /// Ưu tiên dùng trường này khi có thể.
    /// </summary>
    public string? TimeZoneId { get; set; }

    /// <summary>
    /// UTC offset theo giờ (ví dụ: 7, -7, 5.5).
    /// Chỉ dùng khi không cấu hình TimeZoneId.
    /// </summary>
    public double? UtcOffsetHours { get; set; }

    #region Scheduling Configuration

    /// <summary>
    /// Loại lịch trình cho job này (Interval, Daily, Weekly, Monthly).
    /// Mặc định là Interval.
    /// </summary>
    public ScheduleType ScheduleType { get; set; } = ScheduleType.Interval;

    /// <summary>
    /// (Dùng cho ScheduleType.Interval)
    /// Khoảng thời gian giữa các lần chạy, tính bằng giây.
    /// </summary>
    public double TimeIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// (Dùng cho ScheduleType.Daily, Weekly, Monthly)
    /// Thời điểm trong ngày để chạy job, định dạng "HH:mm:ss". Ví dụ: "04:30:00".
    /// </summary>
    public string? TimeOfDay { get; set; }

    /// <summary>
    /// (Dùng cho ScheduleType.Weekly)
    /// Ngày trong tuần để chạy job.
    /// </summary>
    public DayOfWeek? DayOfWeek { get; set; }

    /// <summary>
    /// (Dùng cho ScheduleType.Monthly)
    /// Ngày trong tháng để chạy job (1-31).
    /// Sử dụng một số lớn (ví dụ: 99) để chỉ ngày cuối cùng của tháng.
    /// </summary>
    public int? DayOfMonth { get; set; }

    /// <summary>
    /// Nếu là true, job sẽ chạy một lần ngay khi được khởi động/kích hoạt, sau đó mới tuân theo lịch trình.
    /// </summary>
    public bool RunOnStart { get; set; } = false;

    #endregion

    #region Execution Configuration

    /// <summary>
    /// Số lượng tác vụ có thể chạy song song cho job này.
    /// </summary>
    public int MaxParallel { get; set; } = 1;

    /// <summary>
    /// Tên assembly nơi định nghĩa job.
    /// </summary>
    public string AssemblyName { get; set; } = string.Empty;

    /// <summary>
    /// Dữ liệu JSON được nhúng để truyền vào instance của job.
    /// </summary>
    public string EmbeddedData { get; set; } = string.Empty;

    #endregion

    #region Equality Implementation

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

    public override bool Equals(object? obj)
    {
        return Equals(obj as JobConfig);
    }

    public override int GetHashCode()
    {
        var hashCode = new HashCode();
        hashCode.Add(Name);
        hashCode.Add(IsActive);
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

public enum ScheduleType
{
    Interval,
    Daily,
    Weekly,
    Monthly,
}
