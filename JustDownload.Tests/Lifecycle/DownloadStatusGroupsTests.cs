using FluentAssertions;
using JustDownload.Core.Lifecycle;
using Xunit;

namespace JustDownload.Tests.Lifecycle;

/// <summary>Unit tests for the pure Completed/Incomplete grouping rule (TASK-045 AC0, US-8).</summary>
public sealed class DownloadStatusGroupsTests
{
    [Fact]
    public void Of_OnlyCompleted_IsCompleted()
    {
        DownloadStatusGroups.Of(DownloadStatus.Completed).Should().Be(DownloadStatusGroup.Completed);
    }

    [Theory]
    [InlineData(DownloadStatus.Queued)]
    [InlineData(DownloadStatus.Active)]
    [InlineData(DownloadStatus.Paused)]
    [InlineData(DownloadStatus.Failed)]
    [InlineData(DownloadStatus.Expired)]
    public void Of_EverythingElse_IsIncomplete(DownloadStatus status)
    {
        DownloadStatusGroups.Of(status).Should().Be(DownloadStatusGroup.Incomplete);
    }

    [Fact]
    public void OfCode_MapsPersistedCodes()
    {
        DownloadStatusGroups.OfCode(DownloadStatusCodes.Completed).Should().Be(DownloadStatusGroup.Completed);
        DownloadStatusGroups.OfCode(DownloadStatusCodes.Paused).Should().Be(DownloadStatusGroup.Incomplete);
        DownloadStatusGroups.OfCode(DownloadStatusCodes.Failed).Should().Be(DownloadStatusGroup.Incomplete);
    }
}
