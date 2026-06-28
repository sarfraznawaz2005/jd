using Avalonia.Controls;
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
/// Headless view tests for the downloads list surface (TASK-051): the grid mounts with the full column set,
/// all columns are sortable, the row context menu is present, and columns are hideable through the header menu.
/// </summary>
public sealed class DownloadsListViewTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 27, 12, 0, 0, TimeSpan.Zero);

    private static MainWindowViewModel BuildViewModel(params Download[] records)
    {
        var manager = Substitute.For<IDownloadManager>();
        var repository = Substitute.For<IDownloadRepository>();
        repository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Download>>(records));
        var categorizer = Substitute.For<IFileCategorizer>();
        categorizer.Categorize(Arg.Any<string?>(), Arg.Any<string?>()).Returns(FileCategory.Video);
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(Now);

        var downloads = new DownloadsListViewModel(
            repository, manager, Substitute.For<IDownloadActions>(),
            Substitute.For<IClipboardService>(), Substitute.For<IFileRevealer>(), categorizer, clock);

        var detail = new DownloadDetailViewModel(manager, Substitute.For<IDownloadActions>());
        var sidebar = new SidebarViewModel(downloads);
        return new MainWindowViewModel(new ThemeService(), Substitute.For<IDensityService>(), new StatusSummaryViewModel(manager), downloads, detail, sidebar);
    }

    private static Download Record(long id) => new()
    {
        Id = id,
        Url = $"https://host{id}.example/file{id}.mkv",
        Filename = $"file{id}.mkv",
        Directory = @"C:\Downloads",
        TotalBytes = 55_050_240,
        Status = DownloadStatusCodes.Active,
        CreatedAt = Now - TimeSpan.FromMinutes(id),
    };

    [AvaloniaFact]
    public void Grid_MountsWithAllColumns_AllSortable()
    {
        var window = new MainWindow { DataContext = BuildViewModel() };
        window.Show();

        DataGrid grid = window.FindControl<DataGrid>("DownloadsGrid")!;
        grid.Should().NotBeNull("the downloads grid is the list pane's content");

        string[] headers = grid.Columns.Select(c => c.Header?.ToString()).ToArray()!;
        headers.Should().Equal("Name", "Size", "Status", "Speed", "ETA", "Added");
        grid.Columns.Should().OnlyContain(c => c.CanUserSort == true, "every column is sortable (AC0)");
        grid.CanUserReorderColumns.Should().BeTrue("columns are reorderable (AC0)");
    }

    [AvaloniaFact]
    public void Grid_HasRowContextMenu_WithTheCoreActions()
    {
        var window = new MainWindow { DataContext = BuildViewModel() };
        window.Show();

        DataGrid grid = window.FindControl<DataGrid>("DownloadsGrid")!;
        grid.ContextMenu.Should().NotBeNull("rows carry a context menu (AC2)");

        string[] headers = grid.ContextMenu!.Items.OfType<MenuItem>().Select(m => m.Header?.ToString()).ToArray()!;
        headers.Should().Contain("Resume").And.Contain("Pause").And.Contain("Remove from list").And.Contain("Copy link");
    }

    [AvaloniaFact]
    public void ColumnsMenu_TogglesColumnVisibility()
    {
        var window = new MainWindow { DataContext = BuildViewModel() };
        window.Show();
        DataGrid grid = window.FindControl<DataGrid>("DownloadsGrid")!;

        ContextMenu menu = MainWindow.CreateColumnsMenu(grid);
        var sizeItem = menu.Items.OfType<MenuItem>().Single(m => m.Header?.ToString() == "Size");
        DataGridColumn sizeColumn = grid.Columns.Single(c => c.Header?.ToString() == "Size");

        sizeColumn.IsVisible.Should().BeTrue();
        sizeItem.IsChecked.Should().BeTrue("the menu mirrors the column's visibility");

        // Unchecking the menu item hides the column (two-way bound).
        sizeItem.IsChecked = false;
        sizeColumn.IsVisible.Should().BeFalse("columns are hideable (AC0)");
    }

    [AvaloniaFact]
    public async Task Grid_ShowsRows_WhenDownloadsLoad()
    {
        var vm = BuildViewModel(Record(1), Record(2));
        var window = new MainWindow { DataContext = vm };
        window.Show();
        await vm.Downloads.LoadAsync();

        DataGrid grid = window.FindControl<DataGrid>("DownloadsGrid")!;
        grid.IsVisible.Should().BeTrue("the grid shows once there are downloads");
        window.FindControl<StackPanel>("EmptyState")!.IsVisible.Should().BeFalse();
        vm.Downloads.Downloads.Should().HaveCount(2);
    }
}
