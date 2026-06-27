using Avalonia.Headless.XUnit;
using FluentAssertions;
using JustDownload.App.Services;
using JustDownload.App.ViewModels;
using JustDownload.Core.Abstractions;
using JustDownload.Core.Categorization;
using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.Lifecycle;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>
/// Tests for the category-tree sidebar (TASK-050): it builds the type categories + Status group, shows live
/// count badges per node, and selecting a node filters the downloads list.
/// </summary>
public sealed class SidebarViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 28, 12, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public IDownloadRepository Repository { get; } = Substitute.For<IDownloadRepository>();
        public IDownloadManager Manager { get; } = Substitute.For<IDownloadManager>();
        public IFileCategorizer Categorizer { get; } = Substitute.For<IFileCategorizer>();

        public Harness(params Download[] records)
        {
            Repository.GetAllAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<Download>>(records));
            // Categorize by a simple extension map so different rows land in different categories.
            Categorizer.Categorize(Arg.Any<string?>(), Arg.Any<string?>()).Returns(ci =>
            {
                string? name = (string?)ci[0];
                return name switch
                {
                    not null when name.EndsWith(".mp4", StringComparison.Ordinal) => FileCategory.Video,
                    not null when name.EndsWith(".mp3", StringComparison.Ordinal) => FileCategory.Audio,
                    not null when name.EndsWith(".iso", StringComparison.Ordinal) => FileCategory.Compressed,
                    _ => FileCategory.Other,
                };
            });
        }

        public DownloadsListViewModel BuildList()
        {
            var clock = Substitute.For<IClock>();
            clock.UtcNow.Returns(Now);
            return new DownloadsListViewModel(
                Repository, Manager, Substitute.For<IDownloadActions>(), Substitute.For<IClipboardService>(),
                Substitute.For<IFileRevealer>(), Categorizer, clock);
        }
    }

    private static Download Record(long id, string filename, string status) => new()
    {
        Id = id,
        Url = $"https://host/{filename}",
        Filename = filename,
        Directory = @"C:\Downloads",
        TotalBytes = 1000,
        Status = status,
        CreatedAt = Now,
    };

    [AvaloniaFact]
    public void BuildsTypeCategories_AndStatusGroup()
    {
        var list = new Harness().BuildList();
        var sidebar = new SidebarViewModel(list);

        sidebar.All.Label.Should().Be("All Downloads");
        sidebar.Nodes.Select(n => n.Label).Should()
            .Equal("All Downloads", "Video", "Audio", "Documents", "Compressed", "Programs", "Images");
        sidebar.StatusNodes.Select(n => n.Label).Should().Equal("Incomplete", "Completed");
        sidebar.All.IsSelected.Should().BeTrue("All Downloads is selected by default");
    }

    [AvaloniaFact]
    public async Task CountBadges_ReflectTheDownloads()
    {
        var h = new Harness(
            Record(1, "a.mp4", DownloadStatusCodes.Active),
            Record(2, "b.mp4", DownloadStatusCodes.Completed),
            Record(3, "c.mp3", DownloadStatusCodes.Paused),
            Record(4, "d.iso", DownloadStatusCodes.Completed));
        var list = h.BuildList();
        var sidebar = new SidebarViewModel(list);

        await list.LoadAsync();

        sidebar.All.Count.Should().Be(4);
        sidebar.Nodes.Single(n => n.Label == "Video").Count.Should().Be(2);
        sidebar.Nodes.Single(n => n.Label == "Audio").Count.Should().Be(1);
        sidebar.Nodes.Single(n => n.Label == "Compressed").Count.Should().Be(1);
        sidebar.Incomplete.Count.Should().Be(2, "active + paused are incomplete");
        sidebar.Completed.Count.Should().Be(2);
    }

    [AvaloniaFact]
    public async Task SelectingCategory_FiltersTheList()
    {
        var h = new Harness(
            Record(1, "a.mp4", DownloadStatusCodes.Active),
            Record(2, "b.mp3", DownloadStatusCodes.Active),
            Record(3, "c.mp4", DownloadStatusCodes.Completed));
        var list = h.BuildList();
        var sidebar = new SidebarViewModel(list);
        await list.LoadAsync();

        list.Downloads.Should().HaveCount(3, "All Downloads shows everything");

        sidebar.SelectCommand.Execute(sidebar.Nodes.Single(n => n.Label == "Video"));

        list.Downloads.Should().HaveCount(2, "only the two .mp4 rows match Video");
        list.Downloads.Should().OnlyContain(r => r.Category == FileCategory.Video);
        sidebar.Nodes.Single(n => n.Label == "Video").IsSelected.Should().BeTrue();
        sidebar.All.IsSelected.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task SelectingStatus_FiltersTheList()
    {
        var h = new Harness(
            Record(1, "a.mp4", DownloadStatusCodes.Active),
            Record(2, "b.mp3", DownloadStatusCodes.Completed));
        var list = h.BuildList();
        var sidebar = new SidebarViewModel(list);
        await list.LoadAsync();

        sidebar.SelectCommand.Execute(sidebar.Completed);

        list.Downloads.Should().ContainSingle().Which.Id.Should().Be(2);
    }

    [AvaloniaFact]
    public async Task StatusChange_MovesRowBetweenFilters_AndUpdatesCounts()
    {
        var h = new Harness(Record(1, "a.mp4", DownloadStatusCodes.Active));
        var list = h.BuildList();
        var sidebar = new SidebarViewModel(list);
        await list.LoadAsync();
        sidebar.SelectCommand.Execute(sidebar.Completed);
        list.Downloads.Should().BeEmpty("the only download is still active");

        // The download completes — it should appear under Completed and leave Incomplete.
        h.Manager.StatusChanged += Raise.Event<EventHandler<DownloadStatusChangedEventArgs>>(
            h.Manager, new DownloadStatusChangedEventArgs(1, DownloadStatus.Active, DownloadStatus.Completed));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        list.Downloads.Should().ContainSingle().Which.Id.Should().Be(1);
        sidebar.Completed.Count.Should().Be(1);
        sidebar.Incomplete.Count.Should().Be(0);
    }
}
