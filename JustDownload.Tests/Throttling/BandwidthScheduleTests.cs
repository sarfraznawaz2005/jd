using FluentAssertions;
using JustDownload.Core.Throttling;
using Xunit;

namespace JustDownload.Tests.Throttling;

/// <summary>Time-of-day bandwidth rules (TASK-145): parsing/formatting and the effective-cap resolution.</summary>
public sealed class BandwidthScheduleTests
{
    [Fact]
    public void Parse_ReadsRules_IgnoringMalformed()
    {
        IReadOnlyList<BandwidthRule> rules =
            BandwidthSchedule.Parse("22:00-06:00=0;09:00-17:00=1048576;garbage;25:99-00:00=5");

        rules.Should().HaveCount(2);
        rules[0].Should().Be(new BandwidthRule(new TimeOnly(22, 0), new TimeOnly(6, 0), 0));
        rules[1].BytesPerSecond.Should().Be(1_048_576);
    }

    [Theory]
    [InlineData(23, 0, true)]   // inside the overnight window
    [InlineData(5, 0, true)]    // still inside, after midnight
    [InlineData(7, 0, false)]   // outside
    [InlineData(22, 0, true)]   // inclusive start
    [InlineData(6, 0, false)]   // exclusive end
    public void Rule_Overnight_WrapsPastMidnight(int hour, int minute, bool expected)
    {
        var rule = new BandwidthRule(new TimeOnly(22, 0), new TimeOnly(6, 0), 0);

        rule.IsActiveAt(new TimeOnly(hour, minute)).Should().Be(expected);
    }

    [Fact]
    public void Rule_StartEqualsEnd_IsAllDay()
    {
        var rule = new BandwidthRule(new TimeOnly(0, 0), new TimeOnly(0, 0), 500);

        rule.IsActiveAt(new TimeOnly(3, 0)).Should().BeTrue();
        rule.IsActiveAt(new TimeOnly(18, 30)).Should().BeTrue();
    }

    [Fact]
    public void EffectiveLimit_FirstActiveRuleWins_ElseManual()
    {
        IReadOnlyList<BandwidthRule> rules = BandwidthSchedule.Parse("22:00-06:00=0;09:00-17:00=1000000");

        BandwidthSchedule.EffectiveLimit(rules, new TimeOnly(2, 0), manualLimit: 500).Should().Be(0, "overnight unlimited");
        BandwidthSchedule.EffectiveLimit(rules, new TimeOnly(12, 0), manualLimit: 500).Should().Be(1_000_000, "daytime cap");
        BandwidthSchedule.EffectiveLimit(rules, new TimeOnly(20, 0), manualLimit: 500).Should().Be(500, "no rule -> manual");
    }

    [Fact]
    public void Format_RoundTripsThroughParse()
    {
        var rules = new List<BandwidthRule>
        {
            new(new TimeOnly(22, 0), new TimeOnly(6, 0), 0),
            new(new TimeOnly(9, 30), new TimeOnly(17, 0), 2_000_000),
        };

        string formatted = BandwidthSchedule.Format(rules);

        formatted.Should().Be("22:00-06:00=0;09:30-17:00=2000000");
        BandwidthSchedule.Parse(formatted).Should().BeEquivalentTo(rules);
    }
}
