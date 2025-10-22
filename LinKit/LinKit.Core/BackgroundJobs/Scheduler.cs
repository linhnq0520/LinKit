namespace LinKit.Core.BackgroundJobs;

public static class Scheduler
{
    private static readonly TimeSpan DefaultErrorDelay = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Tính toán thời gian chờ (delay) cho đến lần chạy tiếp theo dựa trên cấu hình job.
    /// </summary>
    /// <param name="config">Cấu hình của job.</param>
    /// <returns>Đối tượng TimeSpan đại diện cho thời gian chờ.</returns>
    public static TimeSpan GetNextDelay(JobConfig config)
    {
        try
        {
            return config.ScheduleType switch
            {
                ScheduleType.Daily => GetNextDailyDelay(config),
                ScheduleType.Weekly => GetNextWeeklyDelay(config),
                ScheduleType.Monthly => GetNextMonthlyDelay(config),
                _ => TimeSpan.FromSeconds(config.TimeIntervalSeconds), // Mặc định là Interval
            };
        }
        catch (Exception) // Bắt lỗi nếu cấu hình không hợp lệ (ví dụ: TimeOfDay sai định dạng)
        {
            return DefaultErrorDelay;
        }
    }

    private static TimeSpan GetNextDailyDelay(JobConfig config)
    {
        if (!TimeSpan.TryParse(config.TimeOfDay, out var timeOfDay))
        {
            return DefaultErrorDelay;
        }

        var now = DateTime.UtcNow;
        var todayScheduledTime = now.Date + timeOfDay;

        DateTime nextRunTime = now >= todayScheduledTime
            ? todayScheduledTime.AddDays(1)
            : todayScheduledTime;

        return nextRunTime - now;
    }

    private static TimeSpan GetNextWeeklyDelay(JobConfig config)
    {
        if (!TimeSpan.TryParse(config.TimeOfDay, out var timeOfDay) || config.DayOfWeek is null)
        {
            return DefaultErrorDelay;
        }

        var now = DateTime.UtcNow;
        var scheduledTimeToday = now.Date + timeOfDay;

        // .NET DayOfWeek: Sunday = 0, Monday = 1, ..., Saturday = 6
        int currentDayOfWeek = (int)now.DayOfWeek;
        int targetDayOfWeek = (int)config.DayOfWeek.Value;

        int daysToAdd = (targetDayOfWeek - currentDayOfWeek + 7) % 7;

        if (daysToAdd == 0 && now >= scheduledTimeToday)
        {
            daysToAdd = 7; // Nếu là hôm nay nhưng đã qua giờ, lên lịch cho tuần sau
        }

        var nextRunDate = now.Date.AddDays(daysToAdd);
        var nextRunTime = nextRunDate + timeOfDay;

        return nextRunTime - now;
    }

    private static TimeSpan GetNextMonthlyDelay(JobConfig config)
    {
        if (!TimeSpan.TryParse(config.TimeOfDay, out var timeOfDay) || config.DayOfMonth is null)
        {
            return DefaultErrorDelay;
        }

        var now = DateTime.UtcNow;
        var nextRunTime = GetNextMonthlyOccurrence(now, config.DayOfMonth.Value, timeOfDay);

        // Nếu lần chạy tính được đã qua, tính cho tháng tiếp theo
        if (now >= nextRunTime)
        {
            // Bắt đầu tìm kiếm từ ngày đầu tiên của tháng sau để tránh lỗi lặp vô hạn
            var searchFrom = new DateTime(now.Year, now.Month, 1).AddMonths(1);
            nextRunTime = GetNextMonthlyOccurrence(searchFrom, config.DayOfMonth.Value, timeOfDay);
        }

        return nextRunTime - now;
    }

    private static DateTime GetNextMonthlyOccurrence(DateTime startTime, int dayOfMonth, TimeSpan timeOfDay)
    {
        int targetDay = dayOfMonth;

        // Xử lý trường hợp "ngày cuối cùng của tháng"
        if (targetDay > 31)
        {
            targetDay = DateTime.DaysInMonth(startTime.Year, startTime.Month);
        }
        else
        {
            // Đảm bảo ngày không vượt quá số ngày trong tháng (ví dụ: ngày 31 tháng 2)
            targetDay = Math.Min(targetDay, DateTime.DaysInMonth(startTime.Year, startTime.Month));
        }

        return new DateTime(startTime.Year, startTime.Month, targetDay, 0, 0, 0, DateTimeKind.Utc) + timeOfDay;
    }
}