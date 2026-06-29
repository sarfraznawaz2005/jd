using FluentAssertions;
using JustDownload.App.Services;
using JustDownload.App.ViewModels.Settings;
using JustDownload.Core.Categorization;
using JustDownload.Core.Security;
using JustDownload.Core.Settings;
using JustDownload.Core.Transport.Proxy;
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
        var vm = new SettingsViewModel(Settings(), Substitute.For<IThemeService>(), CategoryFolderRules.CreateDefault(), Substitute.For<JustDownload.Core.NativeMessaging.INativeHostInstaller>(), Substitute.For<JustDownload.Core.Security.ISecretStore>());

        vm.Sections.Select(s => s.Label).Should()
            .Equal("General", "Connections", "Proxy", "Authentication", "Categories", "Browsers", "Advanced");
        vm.SelectedSection.Label.Should().Be("General");
        vm.Sections[0].IsSelected.Should().BeTrue();
    }

    [Fact]
    public void Select_SwitchesActiveSection()
    {
        var vm = new SettingsViewModel(Settings(), Substitute.For<IThemeService>(), CategoryFolderRules.CreateDefault(), Substitute.For<JustDownload.Core.NativeMessaging.INativeHostInstaller>(), Substitute.For<JustDownload.Core.Security.ISecretStore>());
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
    public void General_DefaultDownloadFolderPersists_AndEmptyClearsToNull()
    {
        ISettingsService settings = Settings();
        var vm = new GeneralSettingsViewModel(settings, Substitute.For<IThemeService>());

        vm.DefaultDownloadFolder = @"  D:\Saved  ";
        Persisted(settings, new AppSettings()).DefaultDownloadDirectory.Should().Be(@"D:\Saved");

        vm.DefaultDownloadFolder = "   ";
        Persisted(settings, new AppSettings { DefaultDownloadDirectory = @"D:\Saved" })
            .DefaultDownloadDirectory.Should().BeNull("a blank folder means 'use the OS Downloads folder'");
    }

    [Fact]
    public void General_HydratesDefaultDownloadFolderFromExistingSettings()
    {
        var current = new AppSettings { DefaultDownloadDirectory = @"E:\Downloads" };
        var vm = new GeneralSettingsViewModel(Settings(current), Substitute.For<IThemeService>());

        vm.DefaultDownloadFolder.Should().Be(@"E:\Downloads");
    }

    [Fact]
    public void General_InvalidDefaultDownloadFolder_ShowsError_AndDoesNotPersist()
    {
        ISettingsService settings = Settings();
        var vm = new GeneralSettingsViewModel(settings, Substitute.For<IThemeService>());

        vm.DefaultDownloadFolder = "C:\\bad\0path"; // the NUL char is invalid on every platform

        vm.DefaultDownloadFolderError.Should().NotBeNull();
        settings.DidNotReceive().UpdateAsync(
            Arg.Any<Func<AppSettings, AppSettings>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void General_MissingDefaultDownloadFolder_ShowsHint_AndStillPersists()
    {
        ISettingsService settings = Settings();
        var vm = new GeneralSettingsViewModel(settings, Substitute.For<IThemeService>());
        string missing = Path.Combine(Path.GetTempPath(), "jd-missing-" + Guid.NewGuid().ToString("N"));

        vm.DefaultDownloadFolder = missing;

        vm.DefaultDownloadFolderError.Should().BeNull();
        vm.DefaultDownloadFolderHint.Should().NotBeNull("a valid but not-yet-existing folder gets a hint");
        Persisted(settings, new AppSettings()).DefaultDownloadDirectory.Should().Be(missing);
    }

    [Fact]
    public void General_ExistingOrEmptyDefaultDownloadFolder_HasNoMessages()
    {
        var vm = new GeneralSettingsViewModel(Settings(), Substitute.For<IThemeService>());

        vm.DefaultDownloadFolder = string.Empty;
        vm.DefaultDownloadFolderError.Should().BeNull();
        vm.DefaultDownloadFolderHint.Should().BeNull();

        vm.DefaultDownloadFolder = Path.GetTempPath(); // exists
        vm.DefaultDownloadFolderError.Should().BeNull();
        vm.DefaultDownloadFolderHint.Should().BeNull();
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
    public void Connections_OutOfRangeValue_ShowsInlineError_AndDoesNotPersist()
    {
        ISettingsService settings = Settings();
        var vm = new ConnectionsSettingsViewModel(settings);

        vm.ConnectionsPerDownload = 999; // above the 32 max
        vm.ConnectionsPerDownloadError.Should().NotBeNull();
        settings.DidNotReceive().UpdateAsync(
            Arg.Any<Func<AppSettings, AppSettings>>(), Arg.Any<CancellationToken>());

        vm.MaxConcurrentDownloads = 0; // below the 1 min
        vm.MaxConcurrentDownloadsError.Should().NotBeNull();

        // A subsequent in-range value clears the error and persists.
        vm.ConnectionsPerDownload = 8;
        vm.ConnectionsPerDownloadError.Should().BeNull();
        Persisted(settings, new AppSettings()).ConnectionsPerDownload.Should().Be(8);
    }

    [Fact]
    public void Connections_SpeedLimitZeroWhenLimited_ShowsInlineError_AndDoesNotPersistZero()
    {
        ISettingsService settings = Settings();
        var vm = new ConnectionsSettingsViewModel(settings);

        vm.SpeedLimited = true;             // persists the default (valid) limit
        vm.SpeedLimitMegabytesPerSecond = 0; // limiting on but no positive speed

        vm.SpeedLimitError.Should().NotBeNull();
        Persisted(settings, new AppSettings()).GlobalSpeedLimitBytesPerSecond
            .Should().NotBe(0, "the invalid 0 MB/s speed is not persisted; the last valid limit stands");

        vm.SpeedLimitMegabytesPerSecond = 3.0; // valid → clears and persists
        vm.SpeedLimitError.Should().BeNull();
        Persisted(settings, new AppSettings()).GlobalSpeedLimitBytesPerSecond.Should().Be(3L * 1024 * 1024);
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

    // --- Proxy (TASK-125) ------------------------------------------------------------------------

    [Fact]
    public async Task Proxy_Save_PersistsConfig_AndStoresPasswordInKeychain()
    {
        ISettingsService settings = Settings();
        var secrets = Substitute.For<ISecretStore>();
        secrets.StoreAsync("s3cret", Arg.Any<CancellationToken>()).Returns("ref-new");
        var vm = new ProxySettingsViewModel(settings, secrets);

        vm.ProxyKind = ProxyKind.Http;
        vm.Host = "proxy.local";
        vm.Port = 8080;
        vm.Username = "user";
        vm.Password = "s3cret";

        vm.SaveCommand.CanExecute(null).Should().BeTrue();
        await vm.SaveCommand.ExecuteAsync(null);

        await secrets.Received(1).StoreAsync("s3cret", Arg.Any<CancellationToken>());
        AppSettings saved = Persisted(settings, new AppSettings());
        saved.ProxyKind.Should().Be(ProxyKind.Http);
        saved.ProxyHost.Should().Be("proxy.local");
        saved.ProxyPort.Should().Be(8080);
        saved.ProxyUsername.Should().Be("user");
        saved.ProxyPasswordSecretRef.Should().Be("ref-new");

        vm.Password.Should().BeEmpty("the plaintext is never kept in the field");
        vm.HasStoredPassword.Should().BeTrue();
        vm.Status.Should().NotBeNull();
    }

    [Fact]
    public async Task Proxy_Save_BlankPassword_KeepsExistingSecretRef()
    {
        var current = new AppSettings
        {
            ProxyKind = ProxyKind.Http,
            ProxyHost = "proxy.local",
            ProxyPort = 8080,
            ProxyUsername = "user",
            ProxyPasswordSecretRef = "old-ref",
        };
        ISettingsService settings = Settings(current);
        var secrets = Substitute.For<ISecretStore>();
        var vm = new ProxySettingsViewModel(settings, secrets);

        vm.HasStoredPassword.Should().BeTrue("hydration reflects a stored password");
        await vm.SaveCommand.ExecuteAsync(null); // password left blank

        await secrets.DidNotReceive().StoreAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Persisted(settings, new AppSettings()).ProxyPasswordSecretRef.Should().Be("old-ref");
    }

    [Fact]
    public async Task Proxy_Save_None_ClearsStoredPassword()
    {
        var current = new AppSettings
        {
            ProxyKind = ProxyKind.Http,
            ProxyHost = "proxy.local",
            ProxyUsername = "user",
            ProxyPasswordSecretRef = "old-ref",
        };
        ISettingsService settings = Settings(current);
        var secrets = Substitute.For<ISecretStore>();
        var vm = new ProxySettingsViewModel(settings, secrets) { ProxyKind = ProxyKind.None };

        await vm.SaveCommand.ExecuteAsync(null);

        await secrets.Received(1).DeleteAsync("old-ref", Arg.Any<CancellationToken>());
        AppSettings saved = Persisted(settings, new AppSettings());
        saved.ProxyKind.Should().Be(ProxyKind.None);
        saved.ProxyPasswordSecretRef.Should().BeNull();
    }

    [Fact]
    public void Proxy_MissingHost_ShowsError_AndBlocksSave()
    {
        var vm = new ProxySettingsViewModel(Settings(), Substitute.For<ISecretStore>())
        {
            ProxyKind = ProxyKind.Http,
            Host = string.Empty,
        };

        vm.ProxyError.Should().NotBeNull();
        vm.SaveCommand.CanExecute(null).Should().BeFalse();
    }
}
