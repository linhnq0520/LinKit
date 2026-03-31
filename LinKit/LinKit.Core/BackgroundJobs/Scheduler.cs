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
                _ => TimeSpan.FromSeconds(config.TimeIntervalSeconds),
            };
        }
        catch (Exception) // Bắt lỗi nếu cấu hình không hợp lệ (ví dụ: TimeOfDay sai định dạng)
        {
            return DefaultErrorDelay;
        }
    }

    private static TimeZoneInfo ResolveTimeZone(JobConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.TimeZoneId))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(config.TimeZoneId);
            }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }

        if (config.UtcOffsetHours is double offsetHours)
        {
            var offset = TimeSpan.FromHours(offsetHours);
            if (offset >= TimeSpan.FromHours(-14) && offset <= TimeSpan.FromHours(14))
            {
                return TimeZoneInfo.CreateCustomTimeZone(
                    id: $"UTC{offset:+hh\\:mm;-hh\\:mm;+00\\:00}",
                    baseUtcOffset: offset,
                    displayName: $"UTC{offset:+hh\\:mm;-hh\\:mm;+00\\:00}",
                    standardDisplayName: $"UTC{offset:+hh\\:mm;-hh\\:mm;+00\\:00}"
                );
            }
        }

        return TimeZoneInfo.Utc;
    }

    private static TimeSpan GetNextDailyDelay(JobConfig config)
    {
        if (!TimeSpan.TryParse(config.TimeOfDay, out var timeOfDay))
        {
            return DefaultErrorDelay;
        }

        var nowUtc = DateTime.UtcNow;
        var timeZone = ResolveTimeZone(config);
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, timeZone);
        var todayScheduledTime = nowLocal.Date + timeOfDay;

        DateTime nextRunLocal =
            nowLocal >= todayScheduledTime ? todayScheduledTime.AddDays(1) : todayScheduledTime;
        DateTime nextRunUtc = TimeZoneInfo.ConvertTimeToUtc(nextRunLocal, timeZone);

        return nextRunUtc - nowUtc;
    }

    private static TimeSpan GetNextWeeklyDelay(JobConfig config)
    {
        if (!TimeSpan.TryParse(config.TimeOfDay, out var timeOfDay) || config.DayOfWeek is null)
        {
            return DefaultErrorDelay;
        }

        var nowUtc = DateTime.UtcNow;
        var timeZone = ResolveTimeZone(config);
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, timeZone);
        var scheduledTimeToday = nowLocal.Date + timeOfDay;

        // .NET DayOfWeek: Sunday = 0, Monday = 1, ..., Saturday = 6
        int currentDayOfWeek = (int)nowLocal.DayOfWeek;
        int targetDayOfWeek = (int)config.DayOfWeek.Value;

        int daysToAdd = (targetDayOfWeek - currentDayOfWeek + 7) % 7;

        if (daysToAdd == 0 && nowLocal >= scheduledTimeToday)
        {
            daysToAdd = 7; // Nếu là hôm nay nhưng đã qua giờ, lên lịch cho tuần sau
        }

        var nextRunDate = nowLocal.Date.AddDays(daysToAdd);
        var nextRunLocal = nextRunDate + timeOfDay;
        var nextRunUtc = TimeZoneInfo.ConvertTimeToUtc(nextRunLocal, timeZone);

        return nextRunUtc - nowUtc;
    }

    private static TimeSpan GetNextMonthlyDelay(JobConfig config)
    {
        if (!TimeSpan.TryParse(config.TimeOfDay, out var timeOfDay) || config.DayOfMonth is null)
        {
            return DefaultErrorDelay;
        }

        var nowUtc = DateTime.UtcNow;
        var timeZone = ResolveTimeZone(config);
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, timeZone);
        var nextRunLocal = GetNextMonthlyOccurrence(nowLocal, config.DayOfMonth.Value, timeOfDay);

        // Nếu lần chạy tính được đã qua, tính cho tháng tiếp theo
        if (nowLocal >= nextRunLocal)
        {
            // Bắt đầu tìm kiếm từ ngày đầu tiên của tháng sau để tránh lỗi lặp vô hạn
            var searchFrom = new DateTime(nowLocal.Year, nowLocal.Month, 1).AddMonths(1);
            nextRunLocal = GetNextMonthlyOccurrence(searchFrom, config.DayOfMonth.Value, timeOfDay);
        }

        var nextRunUtc = TimeZoneInfo.ConvertTimeToUtc(nextRunLocal, timeZone);
        return nextRunUtc - nowUtc;
    }

    private static DateTime GetNextMonthlyOccurrence(
        DateTime startTime,
        int dayOfMonth,
        TimeSpan timeOfDay
    )
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

        return new DateTime(
                startTime.Year,
                startTime.Month,
                targetDay,
                0,
                0,
                0,
                DateTimeKind.Unspecified
            ) + timeOfDay;
    }
}
