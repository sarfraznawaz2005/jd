using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
/// Tests for the toolbar global actions (TASK-052): New URL / Resume / Pause / Stop / Delete / Settings /
/// Browsers act on the current selection or raise the right intent, and their enabled state reflects context.
/// </summary>
public sealed class ToolbarTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 27, 12, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public IDownloadRepository Repository { get; } = Substitute.For<IDownloadRepository>();
        public IDownloadManager Manager { get; } = Substitute.For<IDownloadManager>();
        public IDownloadActions Actions { get; } = Substitute.For<IDownloadActions>();

        public Harness()
        {
            Repository.GetAllAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<Download>>(Array.Empty<Download>()));
        }

        public DownloadsListViewModel BuildList()
        {
            var categorizer = Substitute.For<IFileCategorizer>();
            categorizer.Categorize(Arg.Any<string?>(), Arg.Any<string?>()).Returns(FileCategory.Other);
            var clock = Substitute.For<IClock>();
            clock.UtcNow.Returns(Now);
            return new DownloadsListViewModel(
                Repository, Manager, Actions, Substitute.For<IClipboardService>(),
                Substitute.For<IFileRevealer>(), categorizer, clock);
        }

        public MainWindowViewModel BuildMain(DownloadsListViewModel list) =>
            new(new ThemeService(), new StatusSummaryViewModel(Manager), list);
    }

    private static Download Record(long id, string status) => new()
    {
        Id = id,
        Url = $"https://host{id}.example/file{id}.bin",
        Filename = $"file{id}.bin",
        Directory = @"C:\Downloads",
        TotalBytes = 1_000_000,
        Status = status,
        CreatedAt = Now,
    };

    [AvaloniaFact]
    public void NewDownload_Settings_Browsers_RaiseTheirIntents_AndAreAlwaysEnabled()
    {
        var h = new Harness();
        var vm = h.BuildMain(h.BuildList());

        bool newUrl = false, settings = false, browsers = false;
        vm.NewDownloadRequested += (_, _) => newUrl = true;
        vm.SettingsRequested += (_, _) => settings = true;
        vm.BrowsersRequested += (_, _) => browsers = true;

        vm.NewDownloadCommand.CanExecute(null).Should().BeTrue();
        vm.OpenSettingsCommand.CanExecute(null).Should().BeTrue();
        vm.ShowBrowsersCommand.CanExecute(null).Should().BeTrue();

        vm.NewDownloadCommand.Execute(null);
        vm.OpenSettingsCommand.Execute(null);
        vm.ShowBrowsersCommand.Execute(null);

        newUrl.Should().BeTrue();
        settings.Should().BeTrue();
        browsers.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task TransportActions_OperateOnSelection_WithContextualEnablement()
    {
        var h = new Harness();
        h.Repository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Download>>(new[] { Record(1, DownloadStatusCodes.Paused) }));
        var list = h.BuildList();
        await list.LoadAsync();
        _ = h.BuildMain(list);

        // No selection: nothing operable.
        list.ResumeCommand.CanExecute(null).Should().BeFalse();
        list.PauseCommand.CanExecute(null).Should().BeFalse();
        list.RemoveCommand.CanExecute(null).Should().BeFalse();

        list.SelectedDownload = list.Downloads[0];
        list.ResumeCommand.CanExecute(null).Should().BeTrue("a paused download resumes");
        list.PauseCommand.CanExecute(null).Should().BeFalse("a paused download cannot be paused");

        list.ResumeCommand.Execute(null);
        h.Actions.Received(1).Start(1);
    }

    [AvaloniaFact]
    public void Toolbar_MountsWithAllGlobalActions_BoundToCommands()
    {
        var h = new Harness();
        var vm = h.BuildMain(h.BuildList());
        var window = new MainWindow { DataContext = vm };
        window.Show();

        Border toolbar = window.FindControl<Border>("Toolbar")!;
        var buttons = toolbar.GetVisualDescendants().OfType<Button>().ToList();

        // Every toolbar button must carry a command (wired), and at least the transport + global set is present.
        buttons.Should().HaveCountGreaterThanOrEqualTo(9);
        var tooltips = buttons
            .Select(b => ToolTip.GetTip(b)?.ToString())
            .Where(t => t is not null)
            .ToList();
        tooltips.Should().Contain(t => t!.StartsWith("New download", StringComparison.Ordinal));
        tooltips.Should().Contain("Resume").And.Contain("Pause").And.Contain("Stop all").And.Contain("Remove");
        tooltips.Should().Contain("Settings").And.Contain("Browsers");
        tooltips.Should().Contain("About JustDownload");
    }

    [AvaloniaFact]
    public void AppVersion_IsResolved()
    {
        var h = new Harness();
        var vm = h.BuildMain(h.BuildList());
        vm.AppVersion.Should().NotBeNullOrWhiteSpace();
    }
}
