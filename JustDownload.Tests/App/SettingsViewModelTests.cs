using FluentAssertions;
using JustDownload.App.Services;
using JustDownload.App.ViewModels.Settings;
using JustDownload.Core.Categorization;
using JustDownload.Core.Settings;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>
/// Tests for the settings screens (TASK-057): all seven sections are present, the bound sections persist
/// through the settings service, and live-adjustable values (theme) apply immediately.
/// </summary>
public sealed class SettingsViewModelTests
{
    private static ISettingsService Settings(AppSettings? current = null)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(current ?? new AppSettings());
        // Echo the mutate applied to the current snapshot so tests can assert the persisted result.
        settings.UpdateAsync(Arg.Any<Func<AppSettings, AppSettings>>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<Func<AppSettings, AppSettings>>()(current ?? new AppSettings())));
        return settings;
    }

    private static AppSettings Persisted(ISettingsService settings, AppSettings seed)
    {
        // Replay the last captured mutate against a known seed to inspect what would be saved.
        Func<AppSettings, AppSettings> mutate = (Func<AppSettings, AppSettings>)settings.ReceivedCalls()
            .Last(c => c.GetMethodInfo().Name == nameof(ISettingsService.UpdateAsync))
            .GetArguments()[0]!;
        return mutate(seed);
    }

    [Fact]
    public void AllSevenSections_ArePresent_InOrder()
    {
        var vm = new SettingsViewModel(Settings(), Substitute.For<IThemeService>(), CategoryFolderRules.CreateDefault(), Substitute.For<JustDownload.Core.NativeMessaging.INativeHostInstaller>());

        vm.Sections.Select(s => s.Label).Should()
            .Equal("General", "Connections", "Proxy", "Authentication", "Categories", "Browsers", "Advanced");
        vm.SelectedSection.Label.Should().Be("General");
        vm.Sections[0].IsSelected.Should().BeTrue();
    }

    [Fact]
    public void Select_SwitchesActiveSection()
    {
        var vm = new SettingsViewModel(Settings(), Substitute.For<IThemeService>(), CategoryFolderRules.CreateDefault(), Substitute.For<JustDownload.Core.NativeMessaging.INativeHostInstaller>());
        SettingsSectionViewModel connections = vm.Sections.Single(s => s.Label == "Connections");

        vm.SelectCommand.Execute(connections);

        vm.SelectedSection.Should().BeSameAs(connections);
        connections.IsSelected.Should().BeTrue();
        vm.Sections[0].IsSelected.Should().BeFalse();
    }

    [Fact]
    public void General_ThemeChange_AppliesLive_AndPersists()
    {
        ISettingsService settings = Settings();
        var theme = Substitute.For<IThemeService>();
        theme.Mode.Returns(ThemeMode.Light);
        var vm = new GeneralSettingsViewModel(settings, theme);

        vm.SelectedTheme = ThemeMode.Dark;

        theme.Received(1).SetMode(ThemeMode.Dark);
        Persisted(settings, new AppSettings()).Theme.Should().Be(AppTheme.Dark);
    }

    [Fact]
    public void General_DensityAndQualityPersist()
    {
        ISettingsService settings = Settings();
        var vm = new GeneralSettingsViewModel(settings, Substitute.For<IThemeService>());

        vm.Density = UiDensity.Compact;
        Persisted(settings, new AppSettings()).Density.Should().Be(UiDensity.Compact);

        vm.DefaultVideoQuality = VideoQuality.P2160;
        Persisted(settings, new AppSettings()).DefaultVideoQuality.Should().Be(VideoQuality.P2160);
    }

    [Fact]
    public void General_TrayTogglesPersist()
    {
        ISettingsService settings = Settings();
        var vm = new GeneralSettingsViewModel(settings, Substitute.For<IThemeService>());

        vm.StartMinimizedToTray = true;
        Persisted(settings, new AppSettings()).StartMinimizedToTray.Should().BeTrue();

        vm.CloseToTray = true;
        Persisted(settings, new AppSettings()).CloseToTray.Should().BeTrue();
    }

    [Fact]
    public void General_HydratesTrayTogglesFromExistingSettings()
    {
        var current = new AppSettings { StartMinimizedToTray = true, CloseToTray = true };
        var vm = new GeneralSettingsViewModel(Settings(current), Substitute.For<IThemeService>());

        vm.StartMinimizedToTray.Should().BeTrue();
        vm.CloseToTray.Should().BeTrue();
    }

    [Fact]
    public void Connections_PersistConnectionsConcurrencyAndSpeed()
    {
        ISettingsService settings = Settings();
        var vm = new ConnectionsSettingsViewModel(settings);

        vm.ConnectionsPerDownload = 16;
        Persisted(settings, new AppSettings()).ConnectionsPerDownload.Should().Be(16);

        vm.MaxConcurrentDownloads = 6;
        Persisted(settings, new AppSettings()).MaxConcurrentDownloads.Should().Be(6);

        vm.SpeedLimited = true;
        vm.SpeedLimitMegabytesPerSecond = 2.0;
        Persisted(settings, new AppSettings()).GlobalSpeedLimitBytesPerSecond.Should().Be(2L * 1024 * 1024);

        vm.SpeedLimited = false;
        Persisted(settings, new AppSettings()).GlobalSpeedLimitBytesPerSecond.Should().Be(0, "unlimited persists as 0");
    }

    [Fact]
    public void Connections_ClampsOutOfRangeValues()
    {
        ISettingsService settings = Settings();
        var vm = new ConnectionsSettingsViewModel(settings);

        vm.ConnectionsPerDownload = 999;
        Persisted(settings, new AppSettings()).ConnectionsPerDownload.Should().Be(32);
    }

    [Fact]
    public void Connections_HydratesFromExistingSettings()
    {
        var current = new AppSettings { ConnectionsPerDownload = 12, GlobalSpeedLimitBytesPerSecond = 3L * 1024 * 1024 };
        var vm = new ConnectionsSettingsViewModel(Settings(current));

        vm.ConnectionsPerDownload.Should().Be(12);
        vm.SpeedLimited.Should().BeTrue();
        vm.SpeedLimitMegabytesPerSecond.Should().BeApproximately(3.0, 0.01);
        vm.SpeedLimitDisplay.Should().Be("3.0 MB/s");
    }

    [Fact]
    public void Categories_PersistOrganizeAndRoot_AndListsFolders()
    {
        ISettingsService settings = Settings();
        var vm = new CategoriesSettingsViewModel(settings, CategoryFolderRules.CreateDefault());

        vm.Folders.Should().Contain(f => f.Category == "Video" && f.Folder == "Video");
        vm.Folders.Should().Contain(f => f.Category == "Program" && f.Folder == "Programs");

        vm.OrganizeByCategory = true;
        Persisted(settings, new AppSettings()).OrganizeByCategory.Should().BeTrue();

        vm.OrganizedRootDirectory = @"D:\Sorted";
        Persisted(settings, new AppSettings()).OrganizedRootDirectory.Should().Be(@"D:\Sorted");
    }

    [Fact]
    public void HydrationDoesNotWriteBack()
    {
        ISettingsService settings = Settings(new AppSettings { ConnectionsPerDownload = 8 });
        _ = new ConnectionsSettingsViewModel(settings);

        settings.DidNotReceive().UpdateAsync(Arg.Any<Func<AppSettings, AppSettings>>(), Arg.Any<CancellationToken>());
    }
}
