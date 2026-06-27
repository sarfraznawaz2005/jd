using FluentAssertions;
using JustDownload.App.ViewModels;
using JustDownload.Core.Categorization;
using JustDownload.Core.Data.Models;
using JustDownload.Core.Lifecycle;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>Unit tests for the per-row presentation logic of the downloads list (TASK-051).</summary>
public sealed class DownloadRowViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 27, 12, 0, 0, TimeSpan.Zero);

    private static Download Record(
        string status = DownloadStatusCodes.Active,
        string? filename = "Thriller.mkv",
        string url = "https://youtube.com/watch?v=abc",
        long? total = 55_050_240,
        string? directory = null) => new()
        {
            Id = 7,
            Url = url,
            Filename = filename,
            Directory = directory,
            TotalBytes = total,
            Status = status,
            CreatedAt = Now - TimeSpan.FromHours(2),
        };

    [Theory]
    [InlineData(DownloadStatus.Active, 0.33, "Downloading · 33%")]
    [InlineData(DownloadStatus.Paused, 0.74, "Paused · 74%")]
    [InlineData(DownloadStatus.Queued, null, "Queued")]
    [InlineData(DownloadStatus.Completed, null, "Complete")]
    [InlineData(DownloadStatus.Failed, null, "Failed")]
    [InlineData(DownloadStatus.Expired, null, "Expired — needs renew")]
    public void BuildLabel_PairsStateWithPercent(DownloadStatus status, double? fraction, string expected) =>
        DownloadRowViewModel.BuildLabel(status, fraction).Should().Be(expected);

    [Fact]
    public void BuildLabel_ActiveWithoutFraction_OmitsPercent() =>
        DownloadRowViewModel.BuildLabel(DownloadStatus.Active, null).Should().Be("Downloading");

    [Fact]
    public void Constructor_DerivesStaticColumns()
    {
        var row = new DownloadRowViewModel(Record(), Now, FileCategory.Video);

        row.Id.Should().Be(7);
        row.FileName.Should().Be("Thriller.mkv");
        row.SubLine.Should().Be("youtube.com");
        row.SizeDisplay.Should().Be("52.5 MB");
        row.AddedDisplay.Should().Be("2h ago");
        row.Category.Should().Be(FileCategory.Video);
    }

    [Fact]
    public void Constructor_WithoutFilename_DerivesNameFromUrl()
    {
        var row = new DownloadRowViewModel(
            Record(filename: null, url: "https://releases.ubuntu.com/ubuntu-24.04.1.iso"), Now, FileCategory.Compressed);

        row.FileName.Should().Be("ubuntu-24.04.1.iso");
    }

    [Fact]
    public void ApplyProgress_UpdatesLiveColumnsAndBar()
    {
        var row = new DownloadRowViewModel(Record(), Now, FileCategory.Video);

        row.ApplyProgress(DownloadProgress.Create(
            DownloadStatus.Active, 18_350_080, 55_050_240, 442_000, resumable: true, connections: 8));

        row.StatusLabel.Should().Be("Downloading · 33%");
        row.ProgressPercent.Should().BeApproximately(33.3, 0.5);
        row.ShowProgressBar.Should().BeTrue();
        row.SpeedDisplay.Should().Contain("KB/s");
        row.EtaDisplay.Should().NotBe("—");
        row.IsDownloading.Should().BeTrue();
    }

    [Fact]
    public void ApplyProgress_WhenComplete_ClearsSpeedAndEta()
    {
        var row = new DownloadRowViewModel(Record(), Now, FileCategory.Video);

        row.ApplyProgress(DownloadProgress.Create(
            DownloadStatus.Completed, 55_050_240, 55_050_240, 0, resumable: true));

        row.SpeedDisplay.Should().Be("—");
        row.EtaDisplay.Should().Be("—");
        row.ShowProgressBar.Should().BeFalse();
        row.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public void ApplyStatus_Pausing_KeepsLastPercentButClearsSpeed()
    {
        var row = new DownloadRowViewModel(Record(), Now, FileCategory.Video);
        row.ApplyProgress(DownloadProgress.Create(
            DownloadStatus.Active, 40_737_177, 55_050_240, 442_000, resumable: true)); // ~74%

        row.ApplyStatus(DownloadStatus.Paused);

        row.StatusLabel.Should().Be("Paused · 74%");
        row.ShowProgressBar.Should().BeTrue();
        row.SpeedDisplay.Should().Be("—");
        row.IsPaused.Should().BeTrue();
        row.IsError.Should().BeFalse();
    }

    [Theory]
    [InlineData(DownloadStatusCodes.Queued, true, false, false)]
    [InlineData(DownloadStatusCodes.Paused, true, false, false)]
    [InlineData(DownloadStatusCodes.Active, false, true, false)]
    [InlineData(DownloadStatusCodes.Failed, true, false, true)]
    [InlineData(DownloadStatusCodes.Expired, false, false, true)]
    [InlineData(DownloadStatusCodes.Completed, false, false, false)]
    public void ActionEligibility_FollowsStatus(string status, bool canResume, bool canPause, bool canRenew)
    {
        var row = new DownloadRowViewModel(Record(status: status), Now, FileCategory.Other);

        row.CanResume.Should().Be(canResume);
        row.CanPause.Should().Be(canPause);
        row.CanRenew.Should().Be(canRenew);
    }

    [Fact]
    public void CanOpenFile_RequiresCompletionAndAKnownPath()
    {
        var incomplete = new DownloadRowViewModel(
            Record(status: DownloadStatusCodes.Completed, directory: null), Now, FileCategory.Other);
        incomplete.CanOpenFile.Should().BeFalse("the destination path is unknown");

        var completed = new DownloadRowViewModel(
            Record(status: DownloadStatusCodes.Completed, directory: @"C:\Downloads"), Now, FileCategory.Other);
        completed.CanOpenFile.Should().BeTrue();
        completed.FilePath.Should().Be(Path.Combine(@"C:\Downloads", "Thriller.mkv"));
    }
}
