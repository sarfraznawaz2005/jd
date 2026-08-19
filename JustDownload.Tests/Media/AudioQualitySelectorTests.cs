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

    [Fact]
    public void Select_PrefersHigherLanguagePreference_EvenWithLowerBandwidth()
    {
        IReadOnlyList<AudioVariant> variants =
        [
            new("dub-hi", 192_000, "hi", LanguagePreference: -1),
            new("original-en", 128_000, "en", LanguagePreference: 10),
        ];

        AudioQualitySelector.Select(variants).Id.Should().Be(
            "original-en", "the original-language track must win even at a lower bitrate than a dub");
    }

    [Fact]
    public void Select_AllLanguagePreferencesNull_FallsBackToHighestBandwidth()
    {
        // Regression protection: DASH/HLS/Twitter extractors never set LanguagePreference, so with every
        // variant tied at the null floor the selector must fall back to pure bitrate ordering, unchanged
        // from before language-awareness was added.
        IReadOnlyList<AudioVariant> variants =
        [
            new("a-low", 96_000, "en"),
            new("a-high", 192_000, "en"),
            new("a-mid", 128_000, "en"),
        ];

        AudioQualitySelector.Select(variants).Id.Should().Be("a-high");
    }
}
