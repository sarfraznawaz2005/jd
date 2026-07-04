using FluentAssertions;
using JustDownload.App.Services;
using JustDownload.App.ViewModels.Settings;
using JustDownload.Core.Abstractions;
using JustDownload.Core.Categorization;
using JustDownload.Core.Media;
using JustDownload.Core.Security;
using JustDownload.Core.Settings;
using JustDownload.Core.Transport.Proxy;
using JustDownload.Core.Updates;
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

    private static IAutostartService Autostart(bool isSupported = true)
    {
        var autostart = Substitute.For<IAutostartService>();
        autostart.IsSupported.Returns(isSupported);
        return autostart;
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
    public void AllNineSections_ArePresent_InOrder()
    {
        var vm = Build(Settings(), Substitute.For<ISettingsTransfer>());

        vm.Sections.Select(s => s.Label).Should()
            .Equal("General", "Video", "Connections", "Proxy", "Authentication", "Categories", "Browsers", "Advanced", "Updates");
        vm.SelectedSection.Label.Should().Be("General");
        vm.Sections[0].IsSelected.Should().BeTrue();
    }

    [Fact]
    public void Select_SwitchesActiveSection()
    {
        var vm = Build(Settings(), Substitute.For<ISettingsTransfer>());
        SettingsSectionViewModel connections = vm.Sections.Single(s => s.Label == "Connections");

        vm.SelectCommand.Execute(connections);

        vm.SelectedSection.Should().BeSameAs(connections);
        connections.IsSelected.Should().BeTrue();
        vm.Sections[0].IsSelected.Should().BeFalse();
    }

    private static SettingsViewModel Build(ISettingsService settings, ISettingsTransfer transfer) =>
        new(settings, Substitute.For<IThemeService>(), CategoryFolderRules.CreateDefault(),
            Substitute.For<JustDownload.Core.NativeMessaging.INativeHostInstaller>(),
            Substitute.For<JustDownload.Core.NativeMessaging.IExtensionContactTracker>(),
            Substitute.For<ISecretStore>(), transfer, Substitute.For<IProxyTester>(),
            Substitute.For<JustDownload.Core.IPortableEnvironment>(), Substitute.For<JustDownload.Core.Security.ISavedCredentialsService>(),
            Substitute.For<JustDownload.Core.Media.IYtDlpLocator>(), Substitute.For<JustDownload.Core.Media.IYtDlpProvisioner>(), Autostart(),
            Substitute.For<JustDownload.Core.Updates.IUpdateChecker>(), Substitute.For<JustDownload.Core.Abstractions.IAppVersionProvider>(),
            Substitute.For<JustDownload.Core.Logging.IErrorLogPathProvider>(), Substitute.For<IFileRevealer>());

    [Fact]
    public async Task ExportToAsync_ExportsCurrentSettings_AndReportsSuccess()
    {
        var transfer = Substitute.For<ISettingsTransfer>();
        SettingsViewModel vm = Build(Settings(new AppSettings { AutoExtractArchives = true }), transfer);

        await vm.ExportToAsync("out.json");

        await transfer.Received(1).ExportAsync(
            Arg.Is<AppSettings>(s => s.AutoExtractArchives), "out.json", Arg.Any<CancellationToken>());
        vm.TransferStatus.Should().Be("Settings exported.");
    }

    [Fact]
    public async Task ImportFromAsync_AppliesImported_RebuildsSections_AndReportsSuccess()
    {
        var imported = new AppSettings { Theme = AppTheme.Dark, MonitorClipboard = true };
        var transfer = Substitute.For<ISettingsTransfer>();
        transfer.ImportAsync("in.json", Arg.Any<CancellationToken>()).Returns(imported);
        ISettingsService settings = Settings();
        SettingsViewModel vm = Build(settings, transfer);

        await vm.ImportFromAsync("in.json");

        await settings.Received().UpdateAsync(Arg.Any<Func<AppSettings, AppSettings>>(), Arg.Any<CancellationToken>());
        Persisted(settings, new AppSettings()).Should().Be(imported);
        vm.Sections.Select(s => s.Label).Should()
            .Equal("General", "Video", "Connections", "Proxy", "Authentication", "Categories", "Browsers", "Advanced", "Updates");
        vm.SelectedSection.Label.Should().Be("General");
        vm.TransferStatus.Should().Be("Settings imported.");
    }

    [Fact]
    public async Task ImportFromAsync_ReportsFailure_OnInvalidFile()
    {
        var transfer = Substitute.For<ISettingsTransfer>();
        transfer.When(t => t.ImportAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidDataException("not a valid export"));
        SettingsViewModel vm = Build(Settings(), transfer);

        await vm.ImportFromAsync("in.json");

        vm.TransferStatus.Should().StartWith("Import failed");
    }

    [Fact]
    public void General_ThemeChange_AppliesLive_AndPersists()
    {
        ISettingsService settings = Settings();
        var theme = Substitute.For<IThemeService>();
        theme.Mode.Returns(ThemeMode.Light);
        var vm = new GeneralSettingsViewModel(settings, theme, Substitute.For<JustDownload.Core.IPortableEnvironment>(), Autostart());

        vm.SelectedTheme = ThemeMode.Dark;

        theme.Received(1).SetMode(ThemeMode.Dark);
        Persisted(settings, new AppSettings()).Theme.Should().Be(AppTheme.Dark);
    }

    [Fact]
    public void General_DensityAndQualityPersist()
    {
        ISettingsService settings = Settings();
        var vm = new GeneralSettingsViewModel(settings, Substitute.For<IThemeService>(), Substitute.For<JustDownload.Core.IPortableEnvironment>(), Autostart());

        vm.Density = UiDensity.Compact;
        Persisted(settings, new AppSettings()).Density.Should().Be(UiDensity.Compact);

        vm.DefaultVideoQuality = VideoQuality.P2160;
        Persisted(settings, new AppSettings()).DefaultVideoQuality.Should().Be(VideoQuality.P2160);
    }

    [Fact]
    public void General_TrayTogglesPersist()
    {
        ISettingsService settings = Settings();
        var vm = new GeneralSettingsViewModel(settings, Substitute.For<IThemeService>(), Substitute.For<JustDownload.Core.IPortableEnvironment>(), Autostart());

        vm.StartMinimizedToTray = true;
        Persisted(settings, new AppSettings()).StartMinimizedToTray.Should().BeTrue();

        vm.CloseToTray = true;
        Persisted(settings, new AppSettings()).CloseToTray.Should().BeTrue();
    }

    [Fact]
    public void General_HydratesTrayTogglesFromExistingSettings()
    {
        var current = new AppSettings { StartMinimizedToTray = true, CloseToTray = true };
        var vm = new GeneralSettingsViewModel(Settings(current), Substitute.For<IThemeService>(), Substitute.For<JustDownload.Core.IPortableEnvironment>(), Autostart());

        vm.StartMinimizedToTray.Should().BeTrue();
        vm.CloseToTray.Should().BeTrue();
    }

    [Fact]
    public void General_MonitorClipboard_PersistsAndHydrates()
    {
        ISettingsService settings = Settings();
        var vm = new GeneralSettingsViewModel(settings, Substitute.For<IThemeService>(), Substitute.For<JustDownload.Core.IPortableEnvironment>(), Autostart());

        vm.MonitorClipboard.Should().BeFalse("off by default");
        vm.MonitorClipboard = true;
        Persisted(settings, new AppSettings()).MonitorClipboard.Should().BeTrue();

        var hydrated = new GeneralSettingsViewModel(
            Settings(new AppSettings { MonitorClipboard = true }), Substitute.For<IThemeService>(), Substitute.For<JustDownload.Core.IPortableEnvironment>(), Autostart());
        hydrated.MonitorClipboard.Should().BeTrue();
    }

    [Fact]
    public void General_AutoExtractArchives_PersistsAndHydrates()
    {
        ISettingsService settings = Settings();
        var vm = new GeneralSettingsViewModel(settings, Substitute.For<IThemeService>(), Substitute.For<JustDownload.Core.IPortableEnvironment>(), Autostart());

        vm.AutoExtractArchives.Should().BeFalse("opt-in, off by default");
        vm.AutoExtractArchives = true;
        Persisted(settings, new AppSettings()).AutoExtractArchives.Should().BeTrue();

        var hydrated = new GeneralSettingsViewModel(
            Settings(new AppSettings { AutoExtractArchives = true }), Substitute.For<IThemeService>(), Substitute.For<JustDownload.Core.IPortableEnvironment>(), Autostart());
        hydrated.AutoExtractArchives.Should().BeTrue();
    }

    [Fact]
    public void General_ShowTosNotice_PersistsInvertedAsSuppressFlag_AndHydrates()
    {
        // TASK-160 AC2: the toggle reads "Show the notice" (on by default); turning it off sets the stored
        // SuppressTosNotice flag, and turning it back on clears it so the notice shows again.
        ISettingsService settings = Settings();
        var vm = new GeneralSettingsViewModel(settings, Substitute.For<IThemeService>(), Substitute.For<JustDownload.Core.IPortableEnvironment>(), Autostart());

        vm.ShowTosNotice.Should().BeTrue("the notice is shown by default");
        vm.ShowTosNotice = false;
        Persisted(settings, new AppSettings()).SuppressTosNotice.Should().BeTrue();

        ISettingsService suppressedSettings = Settings(new AppSettings { SuppressTosNotice = true });
        var hydrated = new GeneralSettingsViewModel(
            suppressedSettings, Substitute.For<IThemeService>(), Substitute.For<JustDownload.Core.IPortableEnvironment>(), Autostart());
        hydrated.ShowTosNotice.Should().BeFalse();

        hydrated.ShowTosNotice = true;
        Persisted(suppressedSettings, new AppSettings { SuppressTosNotice = true }).SuppressTosNotice.Should().BeFalse();
    }

    [Fact]
    public void General_LaunchAtStartup_PersistsAndHydrates()
    {
        ISettingsService settings = Settings();
        var vm = new GeneralSettingsViewModel(settings, Substitute.For<IThemeService>(), Substitute.For<JustDownload.Core.IPortableEnvironment>(), Autostart());

        vm.LaunchAtStartup.Should().BeFalse("off by default");
        vm.CanLaunchAtStartup.Should().BeTrue("the substitute IAutostartService reports IsSupported, and the vm isn't portable");

        vm.LaunchAtStartup = true;
        Persisted(settings, new AppSettings()).LaunchAtStartup.Should().BeTrue();

        var hydrated = new GeneralSettingsViewModel(
            Settings(new AppSettings { LaunchAtStartup = true }), Substitute.For<IThemeService>(), Substitute.For<JustDownload.Core.IPortableEnvironment>(), Autostart());
        hydrated.LaunchAtStartup.Should().BeTrue();
    }

    [Fact]
    public void General_LaunchAtStartup_DisabledInPortableMode()
    {
        var portable = Substitute.For<JustDownload.Core.IPortableEnvironment>();
        portable.IsPortable.Returns(true);
        var vm = new GeneralSettingsViewModel(Settings(), Substitute.For<IThemeService>(), portable, Autostart(isSupported: true));

        vm.CanLaunchAtStartup.Should().BeFalse("portable mode must not write the registry Run key, even when autostart is supported");
    }

    [Fact]
    public void General_CanLaunchAtStartup_FollowsAutostartServiceIsSupported_NotHardcodedToWindows()
    {
        // TASK-170: this used to hardcode OperatingSystem.IsWindows(), so mac/Linux never got the toggle even
        // after TASK-155 added real IAutostartService implementations for them. Proven via a substitute so the
        // assertion holds regardless of which OS actually runs the test.
        var vm = new GeneralSettingsViewModel(
            Settings(), Substitute.For<IThemeService>(), Substitute.For<JustDownload.Core.IPortableEnvironment>(),
            Autostart(isSupported: true));

        vm.CanLaunchAtStartup.Should().BeTrue();
    }

    [Fact]
    public void General_CanLaunchAtStartup_False_WhenAutostartServiceUnsupported()
    {
        var vm = new GeneralSettingsViewModel(
            Settings(), Substitute.For<IThemeService>(), Substitute.For<JustDownload.Core.IPortableEnvironment>(),
            Autostart(isSupported: false));

        vm.CanLaunchAtStartup.Should().BeFalse();
    }

    [Fact]
    public void General_NotificationsEnabled_DefaultsOn_AndPersists()
    {
        ISettingsService settings = Settings();
        var vm = new GeneralSettingsViewModel(settings, Substitute.For<IThemeService>(), Substitute.For<JustDownload.Core.IPortableEnvironment>(), Autostart());

        vm.NotificationsEnabled.Should().BeTrue("notifications are on by default");

        vm.NotificationsEnabled = false;
        Persisted(settings, new AppSettings()).NotificationsEnabled.Should().BeFalse();
    }

    [Fact]
    public void General_DefaultDownloadFolderPersists_AndEmptyClearsToNull()
    {
        ISettingsService settings = Settings();
        var vm = new GeneralSettingsViewModel(settings, Substitute.For<IThemeService>(), Substitute.For<JustDownload.Core.IPortableEnvironment>(), Autostart());

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
        var vm = new GeneralSettingsViewModel(Settings(current), Substitute.For<IThemeService>(), Substitute.For<JustDownload.Core.IPortableEnvironment>(), Autostart());

        vm.DefaultDownloadFolder.Should().Be(@"E:\Downloads");
    }

    [Fact]
    public void General_InvalidDefaultDownloadFolder_ShowsError_AndDoesNotPersist()
    {
        ISettingsService settings = Settings();
        var vm = new GeneralSettingsViewModel(settings, Substitute.For<IThemeService>(), Substitute.For<JustDownload.Core.IPortableEnvironment>(), Autostart());

        vm.DefaultDownloadFolder = "C:\\bad\0path"; // the NUL char is invalid on every platform

        vm.DefaultDownloadFolderError.Should().NotBeNull();
        settings.DidNotReceive().UpdateAsync(
            Arg.Any<Func<AppSettings, AppSettings>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void General_MissingDefaultDownloadFolder_ShowsHint_AndStillPersists()
    {
        ISettingsService settings = Settings();
        var vm = new GeneralSettingsViewModel(settings, Substitute.For<IThemeService>(), Substitute.For<JustDownload.Core.IPortableEnvironment>(), Autostart());
        string missing = Path.Combine(Path.GetTempPath(), "jd-missing-" + Guid.NewGuid().ToString("N"));

        vm.DefaultDownloadFolder = missing;

        vm.DefaultDownloadFolderError.Should().BeNull();
        vm.DefaultDownloadFolderHint.Should().NotBeNull("a valid but not-yet-existing folder gets a hint");
        Persisted(settings, new AppSettings()).DefaultDownloadDirectory.Should().Be(missing);
    }

    [Fact]
    public void General_ExistingOrEmptyDefaultDownloadFolder_HasNoMessages()
    {
        var vm = new GeneralSettingsViewModel(Settings(), Substitute.For<IThemeService>(), Substitute.For<JustDownload.Core.IPortableEnvironment>(), Autostart());

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
    public void Connections_PerCategoryLimit_PersistsCanonicalString()
    {
        ISettingsService settings = Settings();
        var vm = new ConnectionsSettingsViewModel(settings);

        vm.CategoryLimits.Should().NotBeEmpty();
        CategoryLimitItem video = vm.CategoryLimits.Single(c => c.Category == "Video");
        CategoryLimitItem audio = vm.CategoryLimits.Single(c => c.Category == "Audio");

        video.Limit = 2;
        audio.Limit = 1;

        Persisted(settings, new AppSettings()).CategoryConcurrencyLimits.Should().Be("Audio=1;Video=2");
    }

    [Fact]
    public void Connections_PerCategoryLimit_HydratesFromSettings_AndClearsToNullWhenAllZero()
    {
        ISettingsService settings = Settings(new AppSettings { CategoryConcurrencyLimits = "Video=3" });
        var vm = new ConnectionsSettingsViewModel(settings);

        vm.CategoryLimits.Single(c => c.Category == "Video").Limit.Should().Be(3);
        vm.CategoryLimits.Single(c => c.Category == "Audio").Limit.Should().Be(0, "unlisted categories are unlimited");

        // Clearing the only cap persists null (no per-category caps), not an empty string.
        vm.CategoryLimits.Single(c => c.Category == "Video").Limit = 0;
        Persisted(settings, new AppSettings { CategoryConcurrencyLimits = "Video=3" })
            .CategoryConcurrencyLimits.Should().BeNull();
    }

    [Fact]
    public void Connections_BandwidthSchedule_AddEditRemove_Persists()
    {
        ISettingsService settings = Settings();
        var vm = new ConnectionsSettingsViewModel(settings);
        vm.ScheduleRules.Should().BeEmpty();

        vm.AddScheduleRuleCommand.Execute(null); // adds a default 22:00-06:00=0 rule and persists
        vm.ScheduleRules.Should().ContainSingle();
        Persisted(settings, new AppSettings()).BandwidthSchedule.Should().Be("22:00-06:00=0");

        vm.ScheduleRules[0].LimitMegabytesPerSecond = 2; // 2 MB/s = 2097152 bytes
        Persisted(settings, new AppSettings()).BandwidthSchedule.Should().Be("22:00-06:00=2097152");

        vm.RemoveScheduleRuleCommand.Execute(vm.ScheduleRules[0]);
        Persisted(settings, new AppSettings { BandwidthSchedule = "x" }).BandwidthSchedule
            .Should().BeNull("removing the last rule clears the schedule");
    }

    [Fact]
    public void Connections_BandwidthSchedule_HydratesFromSettings()
    {
        var vm = new ConnectionsSettingsViewModel(
            Settings(new AppSettings { BandwidthSchedule = "22:00-06:00=0;09:00-17:00=2097152" }));

        vm.ScheduleRules.Should().HaveCount(2);
        vm.ScheduleRules[0].Start.Should().Be("22:00");
        vm.ScheduleRules[0].End.Should().Be("06:00");
        vm.ScheduleRules[1].LimitMegabytesPerSecond.Should().BeApproximately(2.0, 0.01);
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

    private static AdvancedSettingsViewModel BuildAdvanced(ISettingsService settings) =>
        new(settings, Substitute.For<JustDownload.Core.Logging.IErrorLogPathProvider>(), Substitute.For<IFileRevealer>());

    [Fact]
    public void Advanced_LogLevel_PersistsAndHydrates()
    {
        ISettingsService settings = Settings();
        var vm = BuildAdvanced(settings);

        vm.LogLevel.Should().Be(Microsoft.Extensions.Logging.LogLevel.Information, "default");
        vm.LogLevels.Should().Contain(Microsoft.Extensions.Logging.LogLevel.Debug);

        vm.LogLevel = Microsoft.Extensions.Logging.LogLevel.Warning;
        Persisted(settings, new AppSettings()).MinimumLogLevel
            .Should().Be(Microsoft.Extensions.Logging.LogLevel.Warning);

        var hydrated = BuildAdvanced(
            Settings(new AppSettings { MinimumLogLevel = Microsoft.Extensions.Logging.LogLevel.Error }));
        hydrated.LogLevel.Should().Be(Microsoft.Extensions.Logging.LogLevel.Error);
    }

    [Fact]
    public void Advanced_OnCompletionCommand_PersistsAndHydrates_AndBlankClearsToNull()
    {
        ISettingsService settings = Settings();
        var vm = BuildAdvanced(settings);

        vm.OnCompletionCommand.Should().BeEmpty("none by default");
        vm.OnCompletionCommand = @"C:\Tools\scan.exe";
        Persisted(settings, new AppSettings()).OnCompletionCommand.Should().Be(@"C:\Tools\scan.exe");

        vm.OnCompletionCommand = "   ";
        Persisted(settings, new AppSettings { OnCompletionCommand = "x" }).OnCompletionCommand
            .Should().BeNull("blank disables the hook");

        var hydrated = BuildAdvanced(
            Settings(new AppSettings { OnCompletionCommand = @"C:\Tools\scan.exe" }));
        hydrated.OnCompletionCommand.Should().Be(@"C:\Tools\scan.exe");
    }

    [Fact]
    public void Advanced_ViewErrorLogs_OpensTheFileWhenItExists_OtherwiseRevealsTheFolder()
    {
        var errorLogPath = Substitute.For<JustDownload.Core.Logging.IErrorLogPathProvider>();
        errorLogPath.FilePath.Returns(Path.Combine(Path.GetTempPath(), $"jd-errorlog-test-{Guid.NewGuid():N}.log"));
        var fileRevealer = Substitute.For<IFileRevealer>();
        var vm = new AdvancedSettingsViewModel(Settings(), errorLogPath, fileRevealer);

        // The file doesn't exist (nothing has been logged yet) — reveal where it will appear instead of a
        // silent no-op (OpenFile would do nothing for a missing path).
        vm.ViewErrorLogsCommand.Execute(null);
        fileRevealer.Received(1).RevealInFolder(errorLogPath.FilePath);
        fileRevealer.DidNotReceive().OpenFile(Arg.Any<string>());

        try
        {
            File.WriteAllText(errorLogPath.FilePath, "boom");
            vm.ViewErrorLogsCommand.Execute(null);
            fileRevealer.Received(1).OpenFile(errorLogPath.FilePath);
        }
        finally
        {
            File.Delete(errorLogPath.FilePath);
        }
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
        var vm = new ProxySettingsViewModel(settings, secrets, Substitute.For<IProxyTester>());

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
        var vm = new ProxySettingsViewModel(settings, secrets, Substitute.For<IProxyTester>());

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
        var vm = new ProxySettingsViewModel(settings, secrets, Substitute.For<IProxyTester>()) { ProxyKind = ProxyKind.None };

        await vm.SaveCommand.ExecuteAsync(null);

        await secrets.Received(1).DeleteAsync("old-ref", Arg.Any<CancellationToken>());
        AppSettings saved = Persisted(settings, new AppSettings());
        saved.ProxyKind.Should().Be(ProxyKind.None);
        saved.ProxyPasswordSecretRef.Should().BeNull();
    }

    [Fact]
    public void Proxy_MissingHost_ShowsError_AndBlocksSave()
    {
        var vm = new ProxySettingsViewModel(Settings(), Substitute.For<ISecretStore>(), Substitute.For<IProxyTester>())
        {
            ProxyKind = ProxyKind.Http,
            Host = string.Empty,
        };

        vm.ProxyError.Should().NotBeNull();
        vm.SaveCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Proxy_Test_ProbesEnteredConfig_AndShowsResult()
    {
        var tester = Substitute.For<IProxyTester>();
        tester.TestAsync(Arg.Any<ProxyConfiguration>(), Arg.Any<CancellationToken>())
            .Returns(new ProxyTestResult(true, "Connected through the proxy (204 No Content)."));
        var vm = new ProxySettingsViewModel(Settings(), Substitute.For<ISecretStore>(), tester)
        {
            ProxyKind = ProxyKind.Http,
            Host = "proxy.local",
            Port = 8080,
            Username = "user",
            Password = "pw",
        };

        vm.TestCommand.CanExecute(null).Should().BeTrue();
        await vm.TestCommand.ExecuteAsync(null);

        await tester.Received(1).TestAsync(
            Arg.Is<ProxyConfiguration>(c => c.Kind == ProxyKind.Http && c.Host == "proxy.local" && c.Port == 8080
                && c.Credentials != null && c.Credentials.Username == "user" && c.Credentials.Password == "pw"),
            Arg.Any<CancellationToken>());
        vm.Status.Should().Be("Connected through the proxy (204 No Content).");
    }

    [Fact]
    public async Task Proxy_Test_BlankPassword_ResolvesTheStoredSecret()
    {
        var current = new AppSettings
        {
            ProxyKind = ProxyKind.Http,
            ProxyHost = "proxy.local",
            ProxyPort = 3128,
            ProxyUsername = "user",
            ProxyPasswordSecretRef = "ref1",
        };
        var secrets = Substitute.For<ISecretStore>();
        secrets.RetrieveAsync("ref1", Arg.Any<CancellationToken>()).Returns("stored-pw");
        var tester = Substitute.For<IProxyTester>();
        tester.TestAsync(Arg.Any<ProxyConfiguration>(), Arg.Any<CancellationToken>())
            .Returns(new ProxyTestResult(true, "ok"));
        var vm = new ProxySettingsViewModel(Settings(current), secrets, tester); // password field left blank

        await vm.TestCommand.ExecuteAsync(null);

        await tester.Received(1).TestAsync(
            Arg.Is<ProxyConfiguration>(c => c.Credentials != null && c.Credentials.Password == "stored-pw"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Proxy_Test_IsDisabled_WhenNoProxyConfigured()
    {
        var vm = new ProxySettingsViewModel(Settings(), Substitute.For<ISecretStore>(), Substitute.For<IProxyTester>())
        {
            ProxyKind = ProxyKind.None,
        };

        vm.TestCommand.CanExecute(null).Should().BeFalse();
    }

    // --- Video (TASK-162, D3) ---------------------------------------------------------------------

    [Fact]
    public void Video_CaptureToggle_DefaultsOff_PersistsAndHydrates()
    {
        ISettingsService settings = Settings();
        var locator = Substitute.For<IYtDlpLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns((YtDlpInfo?)null);
        var vm = new VideoSettingsViewModel(settings, locator, Substitute.For<IYtDlpProvisioner>());

        vm.VideoCaptureEnabled.Should().BeFalse("gates the yt-dlp fallback; off by default (AC0)");
        vm.VideoCaptureEnabled = true;
        Persisted(settings, new AppSettings()).VideoCaptureEnabled.Should().BeTrue();

        var hydrated = new VideoSettingsViewModel(
            Settings(new AppSettings { VideoCaptureEnabled = true }), locator, Substitute.For<IYtDlpProvisioner>());
        hydrated.VideoCaptureEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task Video_InitialStatus_ReflectsWhetherYtDlpIsAlreadyLocated()
    {
        var locatorMissing = Substitute.For<IYtDlpLocator>();
        locatorMissing.LocateAsync(Arg.Any<CancellationToken>()).Returns((YtDlpInfo?)null);
        var missing = new VideoSettingsViewModel(Settings(), locatorMissing, Substitute.For<IYtDlpProvisioner>());
        await Task.Delay(10); // let the constructor's fire-and-forget status check complete
        missing.Status.Should().Be(YtDlpStatus.NotInstalled);
        missing.StatusText.Should().Be("Not installed");

        var locatorFound = Substitute.For<IYtDlpLocator>();
        locatorFound.LocateAsync(Arg.Any<CancellationToken>()).Returns(new YtDlpInfo("/usr/bin/yt-dlp", "2026.06.09"));
        var found = new VideoSettingsViewModel(Settings(), locatorFound, Substitute.For<IYtDlpProvisioner>());
        await Task.Delay(10);
        found.Status.Should().Be(YtDlpStatus.Ready);
        found.StatusText.Should().Be("Ready (yt-dlp 2026.06.09)");
    }

    [Fact]
    public async Task Video_Download_ProvisionsAndReportsReady()
    {
        var locator = Substitute.For<IYtDlpLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns((YtDlpInfo?)null);
        var provisioner = Substitute.For<IYtDlpProvisioner>();
        provisioner.EnsureAsync(Arg.Any<CancellationToken>()).Returns(new YtDlpInfo(@"C:\vendor\yt-dlp.exe", "2026.06.09"));
        var vm = new VideoSettingsViewModel(Settings(), locator, provisioner);
        await Task.Delay(10);

        await vm.DownloadCommand.ExecuteAsync(null);

        vm.Status.Should().Be(YtDlpStatus.Ready);
        vm.StatusText.Should().Be("Ready (yt-dlp 2026.06.09)");
        vm.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task Video_Download_ReportsError_OnIntegrityFailure()
    {
        var locator = Substitute.For<IYtDlpLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns((YtDlpInfo?)null);
        var provisioner = Substitute.For<IYtDlpProvisioner>();
        provisioner.EnsureAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<YtDlpInfo?>(new YtDlpException("yt-dlp download failed its integrity check.")));
        var vm = new VideoSettingsViewModel(Settings(), locator, provisioner);
        await Task.Delay(10);

        await vm.DownloadCommand.ExecuteAsync(null);

        vm.Status.Should().Be(YtDlpStatus.Error);
        vm.ErrorMessage.Should().Be("yt-dlp download failed its integrity check.");
        vm.StatusText.Should().Be("Error");
    }

    [Fact]
    public async Task Video_Download_ReportsError_WhenNoSourceForPlatform()
    {
        var locator = Substitute.For<IYtDlpLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns((YtDlpInfo?)null);
        var provisioner = Substitute.For<IYtDlpProvisioner>();
        provisioner.EnsureAsync(Arg.Any<CancellationToken>()).Returns((YtDlpInfo?)null);
        var vm = new VideoSettingsViewModel(Settings(), locator, provisioner);
        await Task.Delay(10);

        await vm.DownloadCommand.ExecuteAsync(null);

        vm.Status.Should().Be(YtDlpStatus.Error);
        vm.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    // --- Updates (TASK-080) -----------------------------------------------------------------------

    private static IAppVersionProvider VersionProvider(string version = "1.0.0")
    {
        var provider = Substitute.For<IAppVersionProvider>();
        provider.CurrentVersion.Returns(version);
        return provider;
    }

    [Fact]
    public void Updates_Toggle_DefaultsOff_PersistsAndHydrates()
    {
        ISettingsService settings = Settings();
        var vm = new UpdateSettingsViewModel(settings, Substitute.For<IUpdateChecker>(), VersionProvider());

        vm.AutoUpdateEnabled.Should().BeFalse("opt-in; off by default (AC0)");
        vm.AutoUpdateEnabled = true;
        Persisted(settings, new AppSettings()).AutoUpdateEnabled.Should().BeTrue();

        var hydrated = new UpdateSettingsViewModel(
            Settings(new AppSettings { AutoUpdateEnabled = true }), Substitute.For<IUpdateChecker>(), VersionProvider());
        hydrated.AutoUpdateEnabled.Should().BeTrue();
    }

    [Fact]
    public void Updates_CheckCommand_DisabledUntilToggleIsOn()
    {
        var vm = new UpdateSettingsViewModel(Settings(), Substitute.For<IUpdateChecker>(), VersionProvider());

        vm.CheckForUpdatesCommand.CanExecute(null).Should().BeFalse("AC2: no check while the feature is off");

        vm.AutoUpdateEnabled = true;
        vm.CheckForUpdatesCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task Updates_CheckCommand_ReportsUpToDate()
    {
        var checker = Substitute.For<IUpdateChecker>();
        checker.CheckAsync(Arg.Any<CancellationToken>())
            .Returns(new UpdateCheckResult(UpdateCheckStatus.UpToDate, "1.0.0"));
        var vm = new UpdateSettingsViewModel(Settings(new AppSettings { AutoUpdateEnabled = true }), checker, VersionProvider())
        {
            AutoUpdateEnabled = true,
        };

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        vm.LastStatus.Should().Be(UpdateCheckStatus.UpToDate);
        vm.StatusText.Should().Be("You're up to date.");
    }

    [Fact]
    public async Task Updates_CheckCommand_ReportsRejection_WithoutThrowing()
    {
        var checker = Substitute.For<IUpdateChecker>();
        checker.CheckAsync(Arg.Any<CancellationToken>())
            .Returns(new UpdateCheckResult(UpdateCheckStatus.RejectedInvalidSignature, "2.0.0"));
        var vm = new UpdateSettingsViewModel(Settings(new AppSettings { AutoUpdateEnabled = true }), checker, VersionProvider())
        {
            AutoUpdateEnabled = true,
        };

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        vm.LastStatus.Should().Be(UpdateCheckStatus.RejectedInvalidSignature);
        vm.StatusText.Should().Contain("rejected");
    }

    [Fact]
    public void Updates_CurrentVersion_ReflectsAppVersionProvider()
    {
        var vm = new UpdateSettingsViewModel(Settings(), Substitute.For<IUpdateChecker>(), VersionProvider("3.2.1"));

        vm.CurrentVersion.Should().Be("3.2.1");
    }
}
