using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
/// Command palette (TASK-056, PRD §2.4.2): the palette filters its commands live and runs the selected one
/// (AC1), opens on Ctrl/Cmd+K (AC0), and exposes the core commands — New URL, jump to category, toggle
/// theme, change limits.
/// </summary>
public sealed class CommandPaletteTests
{
    [Fact]
    public void Query_FiltersResults_ByTitleAndKeywords()
    {
        int runs = 0;
        var palette = new CommandPaletteViewModel(
        [
            new PaletteCommand("New URL…", "Actions", () => runs++, "add", "download"),
            new PaletteCommand("Toggle theme", "Actions", () => { }, "dark", "appearance"),
        ]);

        palette.Results.Should().HaveCount(2, "an empty query matches everything");

        palette.Query = "theme";
        palette.Results.Should().ContainSingle().Which.Title.Should().Be("Toggle theme");

        palette.Query = "download"; // keyword match on the first command
        palette.Results.Should().ContainSingle().Which.Title.Should().Be("New URL…");

        palette.Query = "zzz";
        palette.Results.Should().BeEmpty();
        palette.Selected.Should().BeNull();
    }

    [Fact]
    public void Execute_RunsSelectedCommand_AndCloses()
    {
        int runs = 0;
        var palette = new CommandPaletteViewModel([new PaletteCommand("Do it", "Actions", () => runs++)]);
        palette.Open();
        palette.IsOpen.Should().BeTrue();

        palette.ExecuteCommand.Execute(null); // null → run the current selection

        runs.Should().Be(1);
        palette.IsOpen.Should().BeFalse("running a command closes the palette");
    }

    [Fact]
    public void Open_ResetsQuery_AndSelectsFirst()
    {
        var palette = new CommandPaletteViewModel(
        [
            new PaletteCommand("Alpha", "G", () => { }),
            new PaletteCommand("Beta", "G", () => { }),
        ]);
        palette.Query = "beta";
        palette.Open();

        palette.Query.Should().BeEmpty();
        palette.Selected!.Title.Should().Be("Alpha");
        palette.Results.Should().HaveCount(2);
    }

    [Fact]
    public void MoveSelection_IsClampedToResults()
    {
        var palette = new CommandPaletteViewModel(
        [
            new PaletteCommand("A", "G", () => { }),
            new PaletteCommand("B", "G", () => { }),
        ]);

        palette.MoveSelection(-1);
        palette.Selected!.Title.Should().Be("A");
        palette.MoveSelection(1);
        palette.Selected!.Title.Should().Be("B");
        palette.MoveSelection(5);
        palette.Selected!.Title.Should().Be("B", "selection clamps to the last result");
    }

    // --- Shell wiring ----------------------------------------------------------------------------

    private static MainWindowViewModel BuildShell(IThemeService theme)
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
            theme, Substitute.For<IDensityService>(), new StatusSummaryViewModel(manager), downloads, detail,
            new SidebarViewModel(downloads));
    }

    [Fact]
    public void Shell_PaletteHasCoreCommands()
    {
        MainWindowViewModel vm = BuildShell(new ThemeService());

        IReadOnlyList<string> titles = vm.Palette.Results.Select(c => c.Title).ToList();
        titles.Should().Contain("New URL…");
        titles.Should().Contain("Toggle theme");
        titles.Should().Contain("Change limits…");
        titles.Should().Contain("Go to Video", "jump-to-category commands are present");
    }

    [Fact]
    public void Shell_PaletteNewUrl_RaisesNewDownload()
    {
        MainWindowViewModel vm = BuildShell(new ThemeService());
        bool requested = false;
        vm.NewDownloadRequested += (_, _) => requested = true;

        vm.OpenPaletteCommand.Execute(null);
        vm.Palette.IsOpen.Should().BeTrue();
        PaletteCommand newUrl = vm.Palette.Results.First(c => c.Title == "New URL…");
        vm.Palette.ExecuteCommand.Execute(newUrl);

        requested.Should().BeTrue();
    }

    [AvaloniaFact] // ThemeService applies to Application.Current, which must run on the UI thread.
    public void Shell_PaletteToggleTheme_ChangesTheme()
    {
        var theme = new ThemeService();
        MainWindowViewModel vm = BuildShell(theme);
        ThemeMode before = theme.Mode;

        vm.Palette.ExecuteCommand.Execute(vm.Palette.Results.First(c => c.Title == "Toggle theme"));

        theme.Mode.Should().NotBe(before, "the palette's Toggle theme cycles the theme");
    }

    [AvaloniaFact]
    public void MainWindow_OpensPaletteOnCtrlK()
    {
        MainWindowViewModel vm = BuildShell(new ThemeService());
        var window = new MainWindow { DataContext = vm };
        window.Show();

        window.KeyBindings.Select(k => k.Gesture)
            .Should().Contain(new KeyGesture(Key.K, KeyModifiers.Control), "Ctrl+K opens the palette");

        vm.OpenPaletteCommand.Execute(null);
        vm.Palette.IsOpen.Should().BeTrue();
    }
}
