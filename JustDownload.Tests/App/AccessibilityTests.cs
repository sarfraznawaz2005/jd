using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
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
/// Accessibility tests (TASK-059): primary flows are keyboard-operable (AC0) and icon-only controls expose an
/// automation name to assistive tech (AC2). Contrast (AC1) is covered by <see cref="ContrastTests"/>.
/// </summary>
public sealed class AccessibilityTests
{
    private static MainWindowViewModel BuildViewModel()
    {
        var manager = Substitute.For<IDownloadManager>();
        var repository = Substitute.For<IDownloadRepository>();
        repository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<JustDownload.Core.Data.Models.Download>>(
                Array.Empty<JustDownload.Core.Data.Models.Download>()));
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(new DateTimeOffset(2026, 6, 28, 12, 0, 0, TimeSpan.Zero));
        var downloads = new DownloadsListViewModel(
            repository, manager, Substitute.For<IDownloadActions>(), Substitute.For<IClipboardService>(),
            Substitute.For<IFileRevealer>(), Substitute.For<IFileCategorizer>(), clock);
        var detail = new DownloadDetailViewModel(manager, Substitute.For<IDownloadActions>());
        return new MainWindowViewModel(
            new ThemeService(), Substitute.For<IDensityService>(), new StatusSummaryViewModel(manager), downloads, detail, new SidebarViewModel(downloads));
    }

    [AvaloniaFact]
    public void PrimaryShortcuts_AreBound()
    {
        var window = new MainWindow { DataContext = BuildViewModel() };
        window.Show();

        var gestures = window.KeyBindings.Select(k => k.Gesture).ToList();
        gestures.Should().Contain(new KeyGesture(Key.N, KeyModifiers.Control), "New download is Ctrl+N");
        gestures.Should().Contain(new KeyGesture(Key.Delete), "Remove is Delete");
        gestures.Should().Contain(new KeyGesture(Key.OemComma, KeyModifiers.Control), "Settings is Ctrl+,");
    }

    [AvaloniaFact]
    public void NewDownloadShortcut_InvokesTheCommand()
    {
        var vm = BuildViewModel();
        bool requested = false;
        vm.NewDownloadRequested += (_, _) => requested = true;
        var window = new MainWindow { DataContext = vm };
        window.Show();

        KeyBinding newUrl = window.KeyBindings.Single(k => Equals(k.Gesture, new KeyGesture(Key.N, KeyModifiers.Control)));
        newUrl.Command!.Execute(null);

        requested.Should().BeTrue("Ctrl+N triggers the New download flow");
    }

    [AvaloniaFact]
    public void KeyInteractiveControls_AreFocusable()
    {
        var window = new MainWindow { DataContext = BuildViewModel() };
        window.Show();

        // Every command button in the shell must be reachable by keyboard (Tab).
        var buttons = window.GetVisualDescendants().OfType<Button>().Where(b => b.Command is not null).ToList();
        buttons.Should().NotBeEmpty();
        buttons.Should().OnlyContain(b => b.Focusable, "command buttons must be keyboard-focusable");

        window.FindControl<DataGrid>("DownloadsGrid")!.Focusable.Should().BeTrue("the list is keyboard-navigable");
    }

    [AvaloniaFact]
    public void IconOnlyButtons_ExposeAutomationNames()
    {
        var window = new MainWindow { DataContext = BuildViewModel() };
        window.Show();

        // Sample the icon-only toolbar buttons: each must expose a non-empty automation name.
        var named = new[] { "Toggle sidebar", "Resume", "Pause", "Stop all", "Remove", "Settings", "Toggle details", "Toggle theme" };
        var actualNames = window.GetVisualDescendants().OfType<Button>()
            .Select(b => Avalonia.Automation.AutomationProperties.GetName(b))
            .Where(n => !string.IsNullOrEmpty(n))
            .ToList();

        foreach (string expected in named)
        {
            actualNames.Should().Contain(expected, $"the {expected} button exposes an automation name");
        }
    }

    [AvaloniaFact]
    public void AutomationPeer_ReportsTheControlName()
    {
        var window = new MainWindow { DataContext = BuildViewModel() };
        window.Show();

        Button settings = window.GetVisualDescendants().OfType<Button>()
            .First(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Settings");

        AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(settings);
        peer.GetName().Should().Be("Settings", "the automation peer surfaces the name to assistive tech");
    }
}
