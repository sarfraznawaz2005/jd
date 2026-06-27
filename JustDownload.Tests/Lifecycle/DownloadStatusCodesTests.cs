using FluentAssertions;
using JustDownload.Core.Lifecycle;
using Xunit;

namespace JustDownload.Tests.Lifecycle;

/// <summary>Unit tests for the persisted status-code mapping (TASK-031 AC2 — state persisted as stable codes).</summary>
public sealed class DownloadStatusCodesTests
{
    [Theory]
    [InlineData(DownloadStatus.Queued, "queued")]
    [InlineData(DownloadStatus.Active, "active")]
    [InlineData(DownloadStatus.Paused, "paused")]
    [InlineData(DownloadStatus.Completed, "complete")]
    [InlineData(DownloadStatus.Failed, "error")]
    [InlineData(DownloadStatus.Expired, "expired")]
    public void ToCode_And_Parse_RoundTrip(DownloadStatus status, string code)
    {
        DownloadStatusCodes.ToCode(status).Should().Be(code);
        DownloadStatusCodes.Parse(code).Should().Be(status);
    }

    [Fact]
    public void Parse_IsCaseInsensitive()
    {
        DownloadStatusCodes.Parse("COMPLETE").Should().Be(DownloadStatus.Completed);
    }

    [Fact]
    public void Parse_UnknownCode_Throws()
    {
        Action act = () => DownloadStatusCodes.Parse("bogus");
        act.Should().Throw<FormatException>();
    }
}
