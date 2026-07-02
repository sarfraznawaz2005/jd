using FluentAssertions;
using JustDownload.Core.Media;
using Xunit;

namespace JustDownload.Tests.Media;

/// <summary>Unit tests for best-bitrate audio variant selection (TASK-167).</summary>
public sealed class AudioQualitySelectorTests
{
    [Fact]
    public void Select_PicksHighestBandwidth_WhenMultipleAvailable()
    {
        IReadOnlyList<AudioVariant> variants =
        [
            new("a-low", 96_000, "en"),
            new("a-high", 192_000, "en"),
            new("a-mid", 128_000, "en"),
        ];

        AudioQualitySelector.Select(variants).Id.Should().Be("a-high");
    }

    [Fact]
    public void Select_SingleVariant_ReturnsIt()
    {
        IReadOnlyList<AudioVariant> variants = [new("only", 128_000)];

        AudioQualitySelector.Select(variants).Id.Should().Be("only");
    }

    [Fact]
    public void Select_PrefersKnownBandwidth_OverMissing()
    {
        IReadOnlyList<AudioVariant> variants =
        [
            new("unknown"),
            new("known", 64_000),
        ];

        AudioQualitySelector.Select(variants).Id.Should().Be("known");
    }

    [Fact]
    public void Select_Empty_Throws()
    {
        Action act = () => AudioQualitySelector.Select([]);
        act.Should().Throw<ArgumentException>();
    }
}
