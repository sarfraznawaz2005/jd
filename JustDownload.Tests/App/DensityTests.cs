using Avalonia.Headless.XUnit;
using FluentAssertions;
using JustDownload.App.Services;
using JustDownload.App.ViewModels;
using JustDownload.App.Views;
using JustDownload.Core.Abstractions;
using JustDownload.Core.Categorization;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.Lifecycle;
using JustDownload.Core.Settings;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>
/// UI density toggle (TASK-063, US-16 / PRD §2.4.4): the toggle flips Comfortable ⇄ Compact and persists it
/// (AC0/AC1), and the shell reflects it through the <c>compact</c> style class that drives the denser list.
/// </summary>
public sealed class DensityTests
{
    /// <summary>A minimal in-memory settings service that applies updates and raises Changed.</summary>
    private sealed class FakeSettings : ISettingsService
    {
        public AppSettings Current { get; private set; } = new();

        public event EventHandler<SettingsChangedEventArgs>? Changed;

        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken ct = default)
        {
            AppSettings previous = Current;
            AppSettings updated = mutate(previous);
            if (updated != previous)
            {
                Current = updated;
                Changed?.Invoke(this, new SettingsChangedEventArgs(previous, updated, []));
            }

            return Task.FromResult(Current);
        }
    }

    [Fact]
    public void Toggle_FlipsDensity_AndPersists()
    {
        var settings = new FakeSettings();
        var density = new DensityService(settings);
        int changed = 0;
        density.Changed += (_, _) => changed++;

        density.IsCompact.Should().BeFalse("Comfortable is the default");

        density.Toggle();
        density.Density.Should().Be(UiDensity.Compact);
        settings.Current.Density.Should().Be(UiDensity.Compact, "the choice is persisted");
        changed.Should().Be(1);

        density.Toggle();
        density.Density.Should().Be(UiDensity.Comfortable);
        changed.Should().Be(2);
    }

    [Fact]
    public void DensityService_ReflectsChangesMadeElsewhere()
    {
        var settings = new FakeSettings();
        var density = new DensityService(settings);
        bool raised = false;
        density.Changed += (_, _) => raised = true;

        // e.g. the settings screen changes density directly.
        _ = settings.UpdateAsync(s => s with { Density = UiDensity.Compact });

        density.IsCompact.Should().BeTrue();
        raised.Should().BeTrue();
    }

    [Fact]
    public void ToggleDensityCommand_TogglesThroughTheService()
    {
        var settings = new FakeSettings();
        var density = new DensityService(settings);
        MainWindowViewModel vm = BuildViewModel(density);

        bool isCompactChanged = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.IsCompact))
            {
                isCompactChanged = true;
            }
        };

        vm.ToggleDensityCommand.Execute(null);

        vm.IsCompact.Should().BeTrue();
        settings.Current.Density.Should().Be(UiDensity.Compact);
        isCompactChanged.Should().BeTrue("the shell is notified so it can re-apply the compact class");
    }

    [AvaloniaFact]
    public void MainWindow_CarriesCompactClass_WhenDensityIsCompact()
    {
        var density = Substitute.For<IDensityService>();
        density.IsCompact.Returns(true);
        var window = new MainWindow { DataContext = BuildViewModel(density) };
        window.Show();

        window.Classes.Should().Contain("compact", "the compact density drives the shell style class");
    }

    private static MainWindowViewModel BuildViewModel(IDensityService density)
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
            new ThemeService(), density, new StatusSummaryViewModel(manager), downloads, detail,
            new SidebarViewModel(downloads));
    }
}
