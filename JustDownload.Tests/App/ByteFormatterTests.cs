using FluentAssertions;
using JustDownload.App.Formatting;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>Unit tests for the pure byte/speed formatting used by the status bar and list (TASK-049).</summary>
public sealed class ByteFormatterTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(55_050_240, "52.5 MB")]
    [InlineData(4_509_715_660, "4.2 GB")]
    public void FormatSize_UsesBinaryUnits(long bytes, string expected)
    {
        ByteFormatter.FormatSize(bytes).Should().Be(expected);
    }

    [Fact]
    public void FormatSpeed_AppendsPerSecond()
    {
        ByteFormatter.FormatSpeed(442_000).Should().Be("431.6 KB/s");
    }

    [Fact]
    public void FormatSpeed_NonPositive_IsDash()
    {
        ByteFormatter.FormatSpeed(0).Should().Be("—");
        ByteFormatter.FormatSpeed(-5).Should().Be("—");
    }
}
