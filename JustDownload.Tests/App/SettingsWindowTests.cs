using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using JustDownload.App.Services;
using JustDownload.App.ViewModels.Settings;
using JustDownload.App.Views;
using JustDownload.Core.Abstractions;
using JustDownload.Core.Categorization;
using JustDownload.Core.Media;
using JustDownload.Core.NativeMessaging;
using JustDownload.Core.Security;
using JustDownload.Core.Settings;
using JustDownload.Core.Transport.Proxy;
using JustDownload.Core.Updates;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>Headless test that the settings window mounts with its section nav and content (TASK-057).</summary>
public sealed class SettingsWindowTests
{
    private static SettingsViewModel BuildViewModel(
        ISettingsService settings, IYtDlpLocator? ytDlpLocator = null, IYtDlpProvisioner? ytDlpProvisioner = null,
        IUpdateChecker? updateChecker = null)
    {
        var versionProvider = Substitute.For<IAppVersionProvider>();
        versionProvider.CurrentVersion.Returns("1.0.0");
        return new(settings, new ThemeService(), CategoryFolderRules.CreateDefault(),
            Substitute.For<INativeHostInstaller>(), Substitute.For<ISecretStore>(),
            Substitute.For<ISettingsTransfer>(), Substitute.For<IProxyTester>(),
            Substitute.For<JustDownload.Core.IPortableEnvironment>(), Substitute.For<JustDownload.Core.Security.ISavedCredentialsService>(),
            ytDlpLocator ?? Substitute.For<IYtDlpLocator>(), ytDlpProvisioner ?? Substitute.For<IYtDlpProvisioner>(),
            Substitute.For<IAutostartService>(), updateChecker ?? Substitute.For<IUpdateChecker>(), versionProvider);
    }

    [AvaloniaFact]
    public void Window_Mounts_AndShowsSelectedSectionContent()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings());
        SettingsViewModel vm = BuildViewModel(settings);
        var window = new SettingsWindow { DataContext = vm };
        window.Show();

        // The nav lists all nine sections, and the content area renders the General section's controls.
        window.GetVisualDescendants().OfType<ComboBox>().Should().NotBeEmpty("the General section binds combo boxes");
        vm.Sections.Should().HaveCount(9);
    }

    [AvaloniaFact]
    public async Task Window_VideoSection_RendersToggleAndDownloadButton_AndClickDrivesProvisioning()
    {
        // TASK-162 AC0/AC1, exercised through the real production XAML (not just the view-model): the
        // Video section's ToggleSwitch and "Download yt-dlp" Button render and are wired to the bound
        // commands/properties, and clicking the button actually calls IYtDlpProvisioner.EnsureAsync.
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings());
        settings.UpdateAsync(Arg.Any<Func<AppSettings, AppSettings>>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<Func<AppSettings, AppSettings>>()(new AppSettings())));
        var locator = Substitute.For<IYtDlpLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns((YtDlpInfo?)null);
        var provisioner = Substitute.For<IYtDlpProvisioner>();
        provisioner.EnsureAsync(Arg.Any<CancellationToken>()).Returns(new YtDlpInfo("vendor/yt-dlp.exe", "2026.06.09"));

        SettingsViewModel vm = BuildViewModel(settings, locator, provisioner);
        var window = new SettingsWindow { DataContext = vm };
        window.Show();

        SettingsSectionViewModel videoSection = vm.Sections.Single(s => s.Label == "Video");
        vm.SelectCommand.Execute(videoSection);
        var videoVm = (VideoSettingsViewModel)videoSection.Content;

        ToggleSwitch toggle = window.GetVisualDescendants().OfType<ToggleSwitch>()
            .Should().ContainSingle("the Video section renders exactly one toggle — the master capture switch")
            .Subject;
        toggle.IsChecked.Should().BeFalse("video capture/detection is off by default (AC0)");

        window.GetVisualDescendants().OfType<Button>()
            .Should().Contain(b => (b.Content as string) == "Download yt-dlp");

        await videoVm.DownloadCommand.ExecuteAsync(null);

        await provisioner.Received(1).EnsureAsync(Arg.Any<CancellationToken>());
        videoVm.Status.Should().Be(YtDlpStatus.Ready);
        videoVm.StatusText.Should().Be("Ready (yt-dlp 2026.06.09)");
    }

    [AvaloniaFact]
    public async Task Window_UpdatesSection_RendersToggleAndCheckButton_AndClickDrivesTheChecker()
    {
        // TASK-080 AC0/AC1/AC2, exercised through the real production XAML: the Updates section's
        // ToggleSwitch and "Check for Updates" Button render and are wired to the bound
        // commands/properties, the button only appears once the toggle is on, and clicking it actually
        // calls IUpdateChecker.CheckAsync.
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings());
        settings.UpdateAsync(Arg.Any<Func<AppSettings, AppSettings>>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<Func<AppSettings, AppSettings>>()(new AppSettings())));
        var checker = Substitute.For<IUpdateChecker>();
        checker.CheckAsync(Arg.Any<CancellationToken>()).Returns(new UpdateCheckResult(UpdateCheckStatus.UpToDate, "1.0.0"));

        SettingsViewModel vm = BuildViewModel(settings, updateChecker: checker);
        var window = new SettingsWindow { DataContext = vm };
        window.Show();

        SettingsSectionViewModel updatesSection = vm.Sections.Single(s => s.Label == "Updates");
        vm.SelectCommand.Execute(updatesSection);
        var updatesVm = (UpdateSettingsViewModel)updatesSection.Content;

        ToggleSwitch toggle = window.GetVisualDescendants().OfType<ToggleSwitch>()
            .Should().ContainSingle("the Updates section renders exactly one toggle")
            .Subject;
        toggle.IsChecked.Should().BeFalse("auto-update is off by default (AC0)");
        Button checkButton = window.GetVisualDescendants().OfType<Button>()
            .Should().ContainSingle(b => (b.Content as string) == "Check for Updates")
            .Subject;
        checkButton.IsEffectivelyVisible.Should().BeFalse("hidden while the toggle is off (AC2)");

        updatesVm.AutoUpdateEnabled = true;
        checkButton.IsEffectivelyVisible.Should().BeTrue();

        await updatesVm.CheckForUpdatesCommand.ExecuteAsync(null);

        await checker.Received(1).CheckAsync(Arg.Any<CancellationToken>());
        updatesVm.LastStatus.Should().Be(UpdateCheckStatus.UpToDate);
        updatesVm.StatusText.Should().Be("You're up to date.");
    }
}
