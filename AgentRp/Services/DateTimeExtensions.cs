namespace AgentRp.Services;

public static class DateTimeExtensions
{
    public static string ToShorthandDateWithRelativeDays(this DateTime utcDateTime)
    {
        var localDateTime = utcDateTime.ToLocalTime();
        var today = DateTime.Now.Date;
        var targetDate = localDateTime.Date;
        var deltaDays = (targetDate - today).Days;
        var relativeText = deltaDays switch
        {
            0 => "today",
            -1 => "yesterday",
            1 => "tomorrow",
            < 0 => $"{Math.Abs(deltaDays)} days ago",
            _ => $"in {deltaDays} days"
        };

        return $"{localDateTime:MMM d, yyyy} ({relativeText})";
    }

    public static string ToDurationText(this DateTime startedUtc, DateTime? completedUtc, DateTime? nowUtc = null)
    {
        var effectiveCompletedUtc = completedUtc ?? nowUtc ?? DateTime.UtcNow;
        if (effectiveCompletedUtc < startedUtc)
            effectiveCompletedUtc = startedUtc;

        return FormatDuration(effectiveCompletedUtc - startedUtc);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;

        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";

        if (duration.TotalMinutes >= 1)
            return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";

        return $"{Math.Max(0, (int)Math.Floor(duration.TotalSeconds))}s";
    }
}
