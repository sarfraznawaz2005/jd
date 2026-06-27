using FluentAssertions;
using JustDownload.App.Formatting;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>Unit tests for the pure ETA / relative-time formatting used by the list columns (TASK-051).</summary>
public sealed class TimeFormatterTests
{
    [Theory]
    [InlineData(79, "1:19")]
    [InlineData(363, "6:03")]
    [InlineData(0, "—")]
    [InlineData(5, "0:05")]
    public void FormatEta_RendersMinutesAndSeconds(int seconds, string expected) =>
        TimeFormatter.FormatEta(TimeSpan.FromSeconds(seconds)).Should().Be(expected);

    [Fact]
    public void FormatEta_PastAnHour_IncludesHours() =>
        TimeFormatter.FormatEta(TimeSpan.FromSeconds(3723)).Should().Be("1:02:03");

    [Fact]
    public void FormatEta_NullOrNegative_IsEmDash()
    {
        TimeFormatter.FormatEta(null).Should().Be("—");
        TimeFormatter.FormatEta(TimeSpan.FromSeconds(-10)).Should().Be("—");
    }

    [Fact]
    public void FormatEta_RoundsUp_SoItNeverShowsZeroWhileBytesRemain() =>
        TimeFormatter.FormatEta(TimeSpan.FromMilliseconds(400)).Should().Be("0:01");

    [Fact]
    public void FormatRelative_UnderAMinute_IsNow()
    {
        var now = new DateTimeOffset(2026, 6, 27, 12, 0, 0, TimeSpan.Zero);
        TimeFormatter.FormatRelative(now - TimeSpan.FromSeconds(30), now).Should().Be("now");
    }

    [Fact]
    public void FormatRelative_FutureTimestamp_IsTreatedAsNow()
    {
        var now = new DateTimeOffset(2026, 6, 27, 12, 0, 0, TimeSpan.Zero);
        TimeFormatter.FormatRelative(now + TimeSpan.FromMinutes(5), now).Should().Be("now");
    }

    [Theory]
    [InlineData(5, "5m ago")]
    [InlineData(59, "59m ago")]
    public void FormatRelative_WithinTheHour_IsMinutesAgo(int minutes, string expected)
    {
        var now = new DateTimeOffset(2026, 6, 27, 12, 0, 0, TimeSpan.Zero);
        TimeFormatter.FormatRelative(now - TimeSpan.FromMinutes(minutes), now).Should().Be(expected);
    }

    [Fact]
    public void FormatRelative_WithinTheDay_IsHoursAgo()
    {
        var now = new DateTimeOffset(2026, 6, 27, 12, 0, 0, TimeSpan.Zero);
        TimeFormatter.FormatRelative(now - TimeSpan.FromHours(2), now).Should().Be("2h ago");
    }

    [Fact]
    public void FormatRelative_PreviousCalendarDay_IsYesterday()
    {
        var now = new DateTimeOffset(2026, 6, 27, 12, 0, 0, TimeSpan.Zero);
        // 26h earlier is the previous calendar day (2026-06-26 10:00).
        TimeFormatter.FormatRelative(now - TimeSpan.FromHours(26), now).Should().Be("yesterday");
    }

    [Fact]
    public void FormatRelative_WithinTheWeek_IsDaysAgo()
    {
        var now = new DateTimeOffset(2026, 6, 27, 12, 0, 0, TimeSpan.Zero);
        TimeFormatter.FormatRelative(now - TimeSpan.FromDays(3), now).Should().Be("3d ago");
    }

    [Fact]
    public void FormatRelative_BeyondAWeek_IsShortDate()
    {
        var now = new DateTimeOffset(2026, 6, 27, 12, 0, 0, TimeSpan.Zero);
        var when = new DateTimeOffset(2026, 3, 4, 9, 0, 0, TimeSpan.Zero);
        TimeFormatter.FormatRelative(when, now).Should().Be("Mar 4");
    }
}
