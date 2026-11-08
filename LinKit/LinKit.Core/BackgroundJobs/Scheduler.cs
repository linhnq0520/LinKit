using System.Collections.Concurrent;

namespace LinKit.Core.BackgroundJobs;

/// <summary>
/// Computes how long the background-job manager should wait before the next run of a job.
/// Calculations are time-zone aware: the configured local schedule is converted to UTC, and daylight-saving gaps and
/// overlaps are resolved deterministically.
/// </summary>
public static class Scheduler
{
    private static readonly TimeSpan DefaultErrorDelay = TimeSpan.FromMinutes(5);

    private static readonly ConcurrentDictionary<string, TimeZoneInfo> _tzCache = new();

    /// <summary>
    /// Returns the delay to wait before the next execution of <paramref name="config"/>.
    /// </summary>
    /// <param name="config">Job whose schedule is evaluated.</param>
    /// <returns>
    /// A non-negative delay. When the configuration cannot be evaluated (for example an unparsable
    /// <see cref="JobConfig.TimeOfDay"/>), a five-minute retry delay is returned instead of throwing, so a single bad
    /// job never stops the manager.
    /// </returns>
    public static TimeSpan GetNextDelay(JobConfig config)
    {
        try
        {
            var delay = config.ScheduleType switch
            {
                ScheduleType.Daily => GetNextDailyDelay(config),
                ScheduleType.Weekly => GetNextWeeklyDelay(config),
                ScheduleType.Monthly => GetNextMonthlyDelay(config),
                _ => TimeSpan.FromSeconds(config.TimeIntervalSeconds),
            };

            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }
        catch
        {
            return DefaultErrorDelay;
        }
    }

    #region TimeZone

    private static TimeZoneInfo ResolveTimeZone(JobConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.TimeZoneId))
        {
            return _tzCache.GetOrAdd(
                config.TimeZoneId,
                id =>
                {
                    try
                    {
                        return TimeZoneInfo.FindSystemTimeZoneById(id);
                    }
                    catch
                    {
                        return TimeZoneInfo.Utc;
                    }
                }
            );
        }

        if (config.UtcOffsetHours is double offsetHours)
        {
            var offset = TimeSpan.FromHours(offsetHours);

            if (offset >= TimeSpan.FromHours(-14) && offset <= TimeSpan.FromHours(14))
            {
                var key = $"UTC{FormatOffset(offset)}";

                return _tzCache.GetOrAdd(
                    key,
                    _ =>
                        TimeZoneInfo.CreateCustomTimeZone(
                            id: key,
                            baseUtcOffset: offset,
                            displayName: key,
                            standardDisplayName: key
                        )
                );
            }
        }

        return TimeZoneInfo.Utc;
    }

    private static string FormatOffset(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var abs = offset.Duration();
        return $"{sign}{abs:hh\\:mm}";
    }

    private static DateTime SafeConvertToUtc(DateTime localTime, TimeZoneInfo tz)
    {
        // DST invalid time (spring forward)
        if (tz.IsInvalidTime(localTime))
        {
            localTime = localTime.AddHours(1);
        }

        // Ambiguous local time (daylight saving fall-back): pick the earlier offset so the job runs in the first pass.
        if (tz.IsAmbiguousTime(localTime))
        {
            var offsets = tz.GetAmbiguousTimeOffsets(localTime);
            return new DateTimeOffset(localTime, offsets.Min()).UtcDateTime;
        }

        return TimeZoneInfo.ConvertTimeToUtc(localTime, tz);
    }

    #endregion

    #region Daily

    private static TimeSpan GetNextDailyDelay(JobConfig config)
    {
        if (!TimeSpan.TryParse(config.TimeOfDay, out var timeOfDay))
            return DefaultErrorDelay;

        var nowUtc = DateTime.UtcNow;
        var tz = ResolveTimeZone(config);

        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);

        var scheduledToday = nowLocal.Date + timeOfDay;

        var nextLocal = nowLocal >= scheduledToday ? scheduledToday.AddDays(1) : scheduledToday;

        var nextUtc = SafeConvertToUtc(nextLocal, tz);

        return nextUtc - nowUtc;
    }

    #endregion

    #region Weekly

    private static TimeSpan GetNextWeeklyDelay(JobConfig config)
    {
        if (!TimeSpan.TryParse(config.TimeOfDay, out var timeOfDay) || config.DayOfWeek is null)
            return DefaultErrorDelay;

        var nowUtc = DateTime.UtcNow;
        var tz = ResolveTimeZone(config);

        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);

        int current = (int)nowLocal.DayOfWeek;
        int target = (int)config.DayOfWeek.Value;

        int daysToAdd = (target - current + 7) % 7;

        var scheduledToday = nowLocal.Date + timeOfDay;

        if (daysToAdd == 0 && nowLocal >= scheduledToday)
        {
            daysToAdd = 7;
        }

        var nextLocal = nowLocal.Date.AddDays(daysToAdd) + timeOfDay;

        var nextUtc = SafeConvertToUtc(nextLocal, tz);

        return nextUtc - nowUtc;
    }

    #endregion

    #region Monthly

    private static TimeSpan GetNextMonthlyDelay(JobConfig config)
    {
        if (!TimeSpan.TryParse(config.TimeOfDay, out var timeOfDay) || config.DayOfMonth is null)
            return DefaultErrorDelay;

        var nowUtc = DateTime.UtcNow;
        var tz = ResolveTimeZone(config);

        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);

        var nextLocal = GetNextMonthlyOccurrence(
            nowLocal.Year,
            nowLocal.Month,
            config.DayOfMonth.Value,
            timeOfDay
        );

        if (nowLocal >= nextLocal)
        {
            var nextMonth = nowLocal.AddMonths(1);

            nextLocal = GetNextMonthlyOccurrence(
                nextMonth.Year,
                nextMonth.Month,
                config.DayOfMonth.Value,
                timeOfDay
            );
        }

        var nextUtc = SafeConvertToUtc(nextLocal, tz);

        return nextUtc - nowUtc;
    }

    private static DateTime GetNextMonthlyOccurrence(
        int year,
        int month,
        int dayOfMonth,
        TimeSpan timeOfDay
    )
    {
        int daysInMonth = DateTime.DaysInMonth(year, month);

        int targetDay = dayOfMonth switch
        {
            -1 => daysInMonth, // last day of month
            _ => Math.Clamp(dayOfMonth, 1, daysInMonth),
        };

        return new DateTime(year, month, targetDay, 0, 0, 0, DateTimeKind.Unspecified) + timeOfDay;
    }

    #endregion
}
