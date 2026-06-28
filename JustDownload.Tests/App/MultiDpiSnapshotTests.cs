using Avalonia.Headless.XUnit;
using FluentAssertions;
using JustDownload.App.Services;
using JustDownload.App.ViewModels;
using JustDownload.App.Views;
using JustDownload.Core.Abstractions;
using JustDownload.Core.Categorization;
using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.Lifecycle;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>
/// Headless multi-DPI snapshot harness (TASK-084, KPI K7): the shell lays out with no breaks at every
/// supported DPI scale (100/125/150/200/300%), and a layout-break regression at any scale fails the build.
/// </summary>
public sealed class MultiDpiSnapshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 28, 12, 0, 0, TimeSpan.Zero);

    private static MainWindowViewModel BuildViewModel()
    {
        var manager = Substitute.For<IDownloadManager>();
        var repository = Substitute.For<IDownloadRepository>();
        var records = new List<Download>();
        for (int i = 0; i < 20; i++)
        {
            records.Add(new Download
            {
                Id = i + 1,
                Url = $"https://host/file{i}.bin",
                Filename = $"file{i}.bin",
                Directory = @"C:\Downloads",
                TotalBytes = 1000,
                Status = DownloadStatusCodes.Completed,
                CreatedAt = Now,
            });
        }

        repository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Download>>(records));
        var categorizer = Substitute.For<IFileCategorizer>();
        categorizer.Categorize(Arg.Any<string?>(), Arg.Any<string?>()).Returns(FileCategory.Other);
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(Now);
        var downloads = new DownloadsListViewModel(
            repository, manager, Substitute.For<IDownloadActions>(), Substitute.For<IClipboardService>(),
            Substitute.For<IFileRevealer>(), categorizer, clock);
        var detail = new DownloadDetailViewModel(manager, Substitute.For<IDownloadActions>());
        return new MainWindowViewModel(
            new ThemeService(), Substitute.For<IDensityService>(), new StatusSummaryViewModel(manager),
            downloads, detail, new SidebarViewModel(downloads));
    }

    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    [InlineData(3.0)]
    public void Layout_HasNoBreaks_AtEachScale(double scale)
    {
        MainWindowViewModel vm = BuildViewModel();
        var window = new MainWindow { DataContext = vm };

        DpiSnapshotHarness.LayoutSnapshot snapshot = DpiSnapshotHarness.Capture(window, vm, scale);

        DpiSnapshotHarness.AssertNoLayoutBreaks(snapshot);
    }

    [AvaloniaFact]
    public void AllSupportedScales_RenderWithoutBreaks()
    {
        // One pass over all five scales, proving the full K7 range is covered by the harness.
        DpiSnapshotHarness.Scales.Should().Equal(1.0, 1.25, 1.5, 2.0, 3.0);

        foreach (double scale in DpiSnapshotHarness.Scales)
        {
            MainWindowViewModel vm = BuildViewModel();
            var window = new MainWindow { DataContext = vm };
            DpiSnapshotHarness.LayoutSnapshot snapshot = DpiSnapshotHarness.Capture(window, vm, scale);
            DpiSnapshotHarness.AssertNoLayoutBreaks(snapshot);
        }
    }
}
