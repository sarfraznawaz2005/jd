using System.Globalization;

namespace JustDownload.App.Formatting;

/// <summary>
/// Pure, culture-invariant formatting of the list's time columns (TASK-051): the ETA of an active download
/// and the relative "Added" age of any download. Kept separate from <see cref="ByteFormatter"/> and free of
/// any clock dependency (the caller passes "now") so both functions are deterministic and unit-testable.
/// </summary>
public static class TimeFormatter
{
    /// <summary>
    /// Formats an estimated time-remaining as <c>m:ss</c> (e.g. <c>6:03</c>) or <c>h:mm:ss</c> past an hour.
    /// A missing or non-positive estimate renders as the em-dash placeholder <c>—</c>.
    /// </summary>
    public static string FormatEta(TimeSpan? eta)
    {
        if (eta is not { } remaining || remaining <= TimeSpan.Zero)
        {
            return "—";
        }

        // Round up so a download never displays 0:00 while bytes are still outstanding.
        long totalSeconds = (long)Math.Ceiling(remaining.TotalSeconds);
        long hours = totalSeconds / 3600;
        long minutes = totalSeconds % 3600 / 60;
        long seconds = totalSeconds % 60;

        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:D2}:{seconds:D2}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{seconds:D2}");
    }

    /// <summary>
    /// Formats how long ago <paramref name="when"/> was relative to <paramref name="now"/> for the "Added"
    /// column: <c>now</c> under a minute, <c>Nm ago</c>/<c>Nh ago</c> within the day, <c>yesterday</c>, then
    /// <c>Nd ago</c> within a week, falling back to a short absolute date (e.g. <c>Mar 4</c>). A future
    /// timestamp (clock skew) is treated as <c>now</c>.
    /// </summary>
    public static string FormatRelative(DateTimeOffset when, DateTimeOffset now)
    {
        TimeSpan elapsed = now - when;
        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return "now";
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            int minutes = (int)elapsed.TotalMinutes;
            return string.Create(CultureInfo.InvariantCulture, $"{minutes}m ago");
        }

        if (elapsed < TimeSpan.FromHours(24))
        {
            int hours = (int)elapsed.TotalHours;
            return string.Create(CultureInfo.InvariantCulture, $"{hours}h ago");
        }

        // Day-level differences are measured by calendar date so "yesterday" lines up with civil days,
        // not a flat 24-48h window.
        int dayDelta = (now.UtcDateTime.Date - when.UtcDateTime.Date).Days;
        if (dayDelta <= 1)
        {
            return "yesterday";
        }

        if (dayDelta < 7)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{dayDelta}d ago");
        }

        return when.UtcDateTime.ToString("MMM d", CultureInfo.InvariantCulture);
    }
}
