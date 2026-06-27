using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using JustDownload.App.Services;
using JustDownload.App.ViewModels;
using JustDownload.App.Views;
using JustDownload.Core.Lifecycle;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>
/// Headless tests that the detail surface works both inline and detached (TASK-054 AC0): the shared
/// <see cref="DownloadDetailView"/> hosts the three tabs, and the detached <see cref="DownloadDetailWindow"/>
/// mounts the same view-model.
/// </summary>
public sealed class DownloadDetailViewTests
{
    private static DownloadDetailViewModel BuildViewModel()
    {
        var manager = Substitute.For<IDownloadManager>();
        manager.GetConnections(Arg.Any<long>()).Returns([]);
        return new DownloadDetailViewModel(manager, Substitute.For<IDownloadActions>());
    }

    [AvaloniaFact]
    public void InlineView_Mounts_WithThreeTabs()
    {
        var view = new DownloadDetailView { DataContext = BuildViewModel() };
        var host = new Window { Content = view };
        host.Show();

        TabControl tabs = view.GetVisualDescendants().OfType<TabControl>().Single();
        string[] headers = tabs.Items.OfType<TabItem>().Select(t => t.Header?.ToString()).ToArray()!;
        headers.Should().Equal("Download", "Options", "Connections");
    }

    [AvaloniaFact]
    public void DetachedWindow_Mounts_WithTheDetailView()
    {
        var window = new DownloadDetailWindow { DataContext = BuildViewModel() };
        window.Show();

        window.GetVisualDescendants().OfType<DownloadDetailView>().Should().ContainSingle(
            "the detached window hosts the same detail view as the inline pane");
    }
}
