using FluentAssertions;
using JustDownload.Core.Lifecycle;
using Xunit;

namespace JustDownload.Tests.Lifecycle;

/// <summary>Per-category concurrency cap parsing/formatting (TASK-141).</summary>
public sealed class CategoryConcurrencyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_EmptyOrNull_IsEmpty(string? value) =>
        CategoryConcurrency.Parse(value).Should().BeEmpty();

    [Fact]
    public void Parse_ReadsPositiveCaps_CaseInsensitive_IgnoringMalformedAndNonPositive()
    {
        IReadOnlyDictionary<string, int> caps = CategoryConcurrency.Parse("Video=2; audio=3 ;Program=0;junk;Image=-1;Doc=x");

        caps.Should().HaveCount(2);
        caps["Video"].Should().Be(2);
        caps["AUDIO"].Should().Be(3, "lookup is case-insensitive");
        caps.ContainsKey("Program").Should().BeFalse("a zero cap means unlimited and is dropped");
        caps.ContainsKey("Image").Should().BeFalse("a negative cap is dropped");
    }

    [Fact]
    public void Format_DropsNonPositive_AndIsOrderedAndRoundTrips()
    {
        var caps = new Dictionary<string, int> { ["Video"] = 2, ["Audio"] = 1, ["Program"] = 0 };

        string formatted = CategoryConcurrency.Format(caps);

        formatted.Should().Be("Audio=1;Video=2", "ordered by name, zero dropped");
        CategoryConcurrency.Parse(formatted).Should().BeEquivalentTo(
            new Dictionary<string, int> { ["Audio"] = 1, ["Video"] = 2 });
    }
}
