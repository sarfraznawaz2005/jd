using FluentAssertions;
using JustDownload.Core.Media;
using JustDownload.Core.Settings;
using Xunit;

namespace JustDownload.Tests.Media;

/// <summary>Unit tests for default-quality variant selection (TASK-042 AC1).</summary>
public sealed class VideoQualitySelectorTests
{
    private static readonly IReadOnlyList<VideoVariant> Ladder =
    [
        new("v360", 360),
        new("v720", 720),
        new("v1080", 1080),
        new("v2160", 2160),
    ];

    [Theory]
    [InlineData(VideoQuality.P1080, 1080)]
    [InlineData(VideoQuality.P720, 720)]
    [InlineData(VideoQuality.P2160, 2160)]
    public void Select_PicksExactMatch_WhenAvailable(VideoQuality quality, int expectedHeight)
    {
        VideoQualitySelector.Select(Ladder, quality).Height.Should().Be(expectedHeight);
    }

    [Fact]
    public void Select_PicksHighestBelow_WhenNoExactMatch()
    {
        // 480p requested, ladder has 360/720/… → the highest not exceeding 480 is 360.
        VideoQualitySelector.Select(Ladder, VideoQuality.P480).Height.Should().Be(360);
    }

    [Fact]
    public void Select_FallsBackToSmallest_WhenAllExceedRequested()
    {
        IReadOnlyList<VideoVariant> highOnly = [new("v720", 720), new("v1080", 1080)];
        VideoQualitySelector.Select(highOnly, VideoQuality.P360).Height.Should().Be(720);
    }

    [Fact]
    public void Select_BreaksHeightTies_ByHigherBandwidth()
    {
        IReadOnlyList<VideoVariant> sameHeight =
        [
            new("low", 1080, 3_000_000),
            new("high", 1080, 6_000_000),
        ];

        VideoQualitySelector.Select(sameHeight, VideoQuality.P1080).Id.Should().Be("high");
    }

    [Fact]
    public void Select_Empty_Throws()
    {
        Action act = () => VideoQualitySelector.Select([], VideoQuality.P1080);
        act.Should().Throw<ArgumentException>();
    }
}
