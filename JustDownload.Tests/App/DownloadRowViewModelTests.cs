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

    [Theory]
    [InlineData(DownloadStatus.Active, "Downloading · 106.9 MB")]
    [InlineData(DownloadStatus.Paused, "Paused · 106.9 MB")]
    public void BuildLabel_WithoutFraction_FallsBackToBytesFetched(DownloadStatus status, string expected) =>
        DownloadRowViewModel.BuildLabel(status, fraction: null, downloadedBytes: 112_114_345)
            .Should().Be(expected);

    [Fact]
    public void ApplyProgress_WithUnknownTotal_RunsTheBarIndeterminate()
    {
        var row = new DownloadRowViewModel(Record(total: null), Now, FileCategory.Video);

        row.ApplyProgress(DownloadProgress.Create(
            DownloadStatus.Active, 112_114_345, totalBytes: null, 442_000, resumable: false, connections: 1));

        row.StatusLabel.Should().Be("Downloading · 106.9 MB");
        row.ShowProgressBar.Should().BeTrue();
        row.IsProgressIndeterminate.Should().BeTrue();
        row.ProgressPercent.Should().Be(0);
        row.SpeedDisplay.Should().Contain("KB/s");
    }

    [Fact]
    public void ApplyProgress_WithKnownTotal_KeepsTheBarDeterminate()
    {
        var row = new DownloadRowViewModel(Record(), Now, FileCategory.Video);

        row.ApplyProgress(DownloadProgress.Create(
            DownloadStatus.Active, 18_350_080, 55_050_240, 442_000, resumable: true, connections: 8));

        row.IsProgressIndeterminate.Should().BeFalse();
    }

    [Fact]
    public void ApplyStatus_PausingAnUnknownSizeDownload_StopsTheMarquee()
    {
        var row = new DownloadRowViewModel(Record(total: null), Now, FileCategory.Video);
        row.ApplyProgress(DownloadProgress.Create(
            DownloadStatus.Active, 112_114_345, totalBytes: null, 442_000, resumable: false, connections: 1));

        row.ApplyStatus(DownloadStatus.Paused);

        // A marquee on a paused transfer would animate as though bytes were still moving.
        row.IsProgressIndeterminate.Should().BeFalse();
        row.ShowProgressBar.Should().BeFalse();
        row.StatusLabel.Should().Be("Paused · 106.9 MB");
    }

    [Fact]
    public void ApplyProgress_WhilePostProcessing_NamesTheWorkInsteadOfShowingAFrozenCount()
    {
        var row = new DownloadRowViewModel(Record(total: null), Now, FileCategory.Video);
        row.ApplyProgress(DownloadProgress.Create(
            DownloadStatus.Active, 112_114_345, totalBytes: null, 442_000, resumable: false, connections: 1));

        row.ApplyProgress(new DownloadProgress
        {
            Status = DownloadStatus.Active,
            DownloadedBytes = 112_114_345,
            BytesPerSecond = 0,
            Phase = DownloadPhase.Processing,
        });

        row.StatusLabel.Should().Be("Merging streams…");
        row.IsProgressIndeterminate.Should().BeTrue("the merge has no measurable progress");
        row.ShowProgressBar.Should().BeTrue("work is still happening, it just can't be measured");
        row.SpeedDisplay.Should().Be("—", "no bytes move while streams are being joined");
    }

    [Fact]
    public void ApplyProgress_PostProcessingAKnownSizeDownload_StillDropsTheStalePercentage()
    {
        // HLS counts segments, so its transfer has a real fraction — but that fraction says nothing about
        // how far the mux has got, and leaving the bar at 100% would claim the download was done.
        var row = new DownloadRowViewModel(Record(), Now, FileCategory.Video);

        row.ApplyProgress(new DownloadProgress
        {
            Status = DownloadStatus.Active,
            DownloadedBytes = 55_050_240,
            Fraction = 1.0,
            Phase = DownloadPhase.Processing,
        });

        row.StatusLabel.Should().Be("Merging streams…");
        row.IsProgressIndeterminate.Should().BeTrue();
    }

    [Fact]
    public void ApplyStatus_AfterPostProcessingBegan_KeepsNamingTheWork()
    {
        var row = new DownloadRowViewModel(Record(total: null), Now, FileCategory.Video);
        row.ApplyProgress(new DownloadProgress
        {
            Status = DownloadStatus.Active,
            DownloadedBytes = 112_114_345,
            Phase = DownloadPhase.Processing,
        });

        row.ApplyStatus(DownloadStatus.Active);

        row.StatusLabel.Should().Be("Merging streams…", "a bare status change must not reset the phase");
    }

    [Fact]
    public void ApplyProgress_WhenTheTotalIsFinallyRevealed_AdoptsItIntoTheSizeColumn()
    {
        var row = new DownloadRowViewModel(Record(total: null), Now, FileCategory.Video);
        row.SizeDisplay.Should().Be("—");

        row.ApplyProgress(DownloadProgress.Create(
            DownloadStatus.Completed, 112_114_345, 112_114_345, 0, resumable: false, connections: 1));

        row.SizeDisplay.Should().Be("106.9 MB");
        row.TotalBytes.Should().Be(112_114_345);
    }

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
