using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using FluentAssertions;
using JustDownload.App.Services;
using JustDownload.App.ViewModels;
using JustDownload.App.Views;
using JustDownload.Core.Abstractions;
using JustDownload.Core.Categorization;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.Lifecycle;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>
/// Headless UI tests for the app shell (TASK-047): the window mounts and is usable (AC0), light is the
/// default theme and dark/OS are reachable (AC1), the design tokens are centralized and resolve per variant
/// (AC2), and they carry the mockup's accent (AC3).
/// </summary>
public sealed class ShellTests
{
    [AvaloniaFact]
    public void DefaultThemeIsLight()
    {
        Application.Current!.RequestedThemeVariant.Should().Be(ThemeVariant.Light);
    }

    [AvaloniaFact]
    public void DesignTokens_ResolvePerVariant_AndCarryMockupAccent()
    {
        bool gotLight = Application.Current!.TryGetResource("AccentBrush", ThemeVariant.Light, out object? light);
        bool gotDark = Application.Current!.TryGetResource("AccentBrush", ThemeVariant.Dark, out object? dark);

        gotLight.Should().BeTrue();
        gotDark.Should().BeTrue();

        // The accent matches mockups/styles.css (light #5b67d6) — proving tokens are centralized and applied.
        ((SolidColorBrush)light!).Color.Should().Be(Color.Parse("#5b67d6"));
        ((SolidColorBrush)dark!).Color.Should().Be(Color.Parse("#5e6ad2"));

        // Spacing/radius metrics are present too (theme-independent tokens).
        Application.Current!.TryGetResource("Space4", null, out object? space).Should().BeTrue();
        space.Should().Be(16d);
    }

    private static MainWindowViewModel BuildViewModel()
    {
        var manager = Substitute.For<IDownloadManager>();
        var repository = Substitute.For<IDownloadRepository>();
        repository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<JustDownload.Core.Data.Models.Download>>(
                Array.Empty<JustDownload.Core.Data.Models.Download>()));
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(new DateTimeOffset(2026, 6, 27, 12, 0, 0, TimeSpan.Zero));
        var downloads = new DownloadsListViewModel(
            repository,
            manager,
            Substitute.For<IDownloadActions>(),
            Substitute.For<IClipboardService>(),
            Substitute.For<IFileRevealer>(),
            Substitute.For<IFileCategorizer>(),
            clock);

        var detail = new DownloadDetailViewModel(manager, Substitute.For<IDownloadActions>());
        var sidebar = new SidebarViewModel(downloads);
        return new MainWindowViewModel(new ThemeService(), new StatusSummaryViewModel(manager), downloads, detail, sidebar);
    }

    [AvaloniaFact]
    public void MainWindow_MountsWithShellChrome()
    {
        var window = new MainWindow { DataContext = BuildViewModel() };
        window.Show();

        window.FindControl<Border>("Toolbar").Should().NotBeNull("the toolbar is part of the shell");
        window.FindControl<Border>("Sidebar").Should().NotBeNull("the sidebar is part of the shell");
        window.FindControl<Border>("StatusBar").Should().NotBeNull("the status bar is part of the shell");
        window.FindControl<StackPanel>("EmptyState").Should().NotBeNull("the empty-state placeholder shows");
    }

    [AvaloniaFact]
    public void MainWindow_HasThreePanes_WithResizeAndCollapse()
    {
        var vm = BuildViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();

        Border sidebar = window.FindControl<Border>("Sidebar")!;
        window.FindControl<Border>("ListPane").Should().NotBeNull("the list pane is the master pane");
        window.FindControl<Border>("DetailPane").Should().NotBeNull("the detail pane is the detail master/detail half");
        window.FindControl<GridSplitter>("PaneSplitter").Should().NotBeNull("list and detail are resizable");

        sidebar.IsVisible.Should().BeTrue();
        vm.ToggleSidebarCommand.Execute(null);
        sidebar.IsVisible.Should().BeFalse("toggling collapses the sidebar");
    }

    [AvaloniaFact]
    public void ThemeToggle_SwitchesVariant_ThenRestores()
    {
        ThemeVariant original = Application.Current!.RequestedThemeVariant!;
        try
        {
            var theme = new ThemeService();
            theme.Mode.Should().Be(ThemeMode.Light);

            theme.Toggle();
            theme.Mode.Should().Be(ThemeMode.Dark);
            Application.Current!.RequestedThemeVariant.Should().Be(ThemeVariant.Dark);

            theme.Toggle();
            theme.Mode.Should().Be(ThemeMode.System);
            Application.Current!.RequestedThemeVariant.Should().Be(ThemeVariant.Default);
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = original; // don't leak theme state to other tests
        }
    }
}
