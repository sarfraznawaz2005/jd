using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
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
/// DPI / resolution adaptivity (TASK-048): the layout stays valid and usable from 800x600 to 4K (the sidebar
/// auto-hides when narrow so the list never collapses), icons are vector (crisp at any DPI), and the list
/// virtualizes thousands of rows.
/// </summary>
public sealed class DpiAdaptivityTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 28, 12, 0, 0, TimeSpan.Zero);

    private static MainWindowViewModel BuildViewModel(int rowCount = 0)
    {
        var manager = Substitute.For<IDownloadManager>();
        var repository = Substitute.For<IDownloadRepository>();
        var records = new List<Download>(rowCount);
        for (int i = 0; i < rowCount; i++)
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
            new ThemeService(), Substitute.For<IDensityService>(), new StatusSummaryViewModel(manager), downloads, detail, new SidebarViewModel(downloads));
    }

    [Fact]
    public void Sidebar_AutoHides_WhenNarrow_AndRespectsManualCollapse()
    {
        var vm = BuildViewModel();

        vm.UpdateForWidth(1280);
        vm.SidebarVisible.Should().BeTrue("a wide window shows the sidebar");

        vm.UpdateForWidth(800);
        vm.SidebarVisible.Should().BeFalse("a narrow window auto-hides the sidebar so the list+detail fit");

        vm.UpdateForWidth(1280);
        vm.SidebarCollapsed = true;
        vm.SidebarVisible.Should().BeFalse("a manual collapse still hides it when wide");
    }

    [AvaloniaFact]
    public async Task Layout_IsValid_At800x600()
    {
        // The detail pane only shows once something is selected (it no longer reserves its column with
        // nothing to show), so select the one row to exercise its width alongside the list's.
        var vm = BuildViewModel(rowCount: 1);
        var window = new MainWindow { DataContext = vm, Width = 800, Height = 600 };
        window.Show();
        await vm.Downloads.LoadAsync();
        vm.Detail.Select(vm.Downloads.Downloads[0]);
        vm.UpdateForWidth(800);
        window.UpdateLayout();

        window.FindControl<Border>("Sidebar")!.IsVisible.Should().BeFalse("sidebar auto-hides at 800px");
        Border list = window.FindControl<Border>("ListPane")!;
        Border detail = window.FindControl<Border>("DetailPane")!;
        list.Bounds.Width.Should().BeGreaterThan(300, "the list keeps a usable width at 800x600");
        detail.Bounds.Width.Should().BeGreaterThan(0);
        // Panes do not overflow the window.
        (list.Bounds.Width + detail.Bounds.Width).Should().BeLessThanOrEqualTo(800 + 1);
    }

    [AvaloniaFact]
    public void Layout_IsValid_At4K_WithSidebar()
    {
        var vm = BuildViewModel();
        var window = new MainWindow { DataContext = vm, Width = 3840, Height = 2160 };
        window.Show();
        vm.UpdateForWidth(3840);
        window.UpdateLayout();

        window.FindControl<Border>("Sidebar")!.IsVisible.Should().BeTrue("a 4K window shows the sidebar");
        window.FindControl<Border>("ListPane")!.Bounds.Width.Should().BeGreaterThan(1000, "the list scales up");
    }

    [AvaloniaFact]
    public void Icons_AreVector_NoRasterImages()
    {
        var window = new MainWindow { DataContext = BuildViewModel() };
        window.Show();
        window.UpdateLayout();

        window.GetVisualDescendants().OfType<PathIcon>().Should().NotBeEmpty("the shell uses vector path icons");
        window.GetVisualDescendants().OfType<Image>()
            .Where(i => i.Source is Bitmap)
            .Should().BeEmpty("no raster bitmaps — icons stay crisp at any DPI");
    }

    [AvaloniaFact]
    public async Task Grid_VirtualizesThousandsOfRows()
    {
        var vm = BuildViewModel(rowCount: 2000);
        var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
        window.Show();
        await vm.Downloads.LoadAsync();
        window.UpdateLayout();

        vm.Downloads.Downloads.Should().HaveCount(2000, "all rows are in the data set");
        int realized = window.GetVisualDescendants().OfType<DataGridRow>().Count();
        realized.Should().BeGreaterThan(0).And.BeLessThan(200,
            "the grid realizes only the visible rows, not all 2000 (virtualization)");
    }
}
