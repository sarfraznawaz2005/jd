using System.Runtime.Versioning;
using FluentAssertions;
using JustDownload.App.Services;
using JustDownload.Core.Settings;
using Microsoft.Win32;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.App;

/// <summary>
/// Launch-at-login (TASK-122): the Windows service writes/removes a per-user Run-key entry (verified against
/// an isolated key, not the real one), and the controller reconciles the OS registration with the setting.
/// </summary>
public sealed class AutostartTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public void WindowsAutostart_SetEnabled_WritesThenRemoves_AnIsolatedRunValue()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // the Run-key mechanism is Windows-only
        }

        const string root = @"Software\JustDownloadTests";
        string keyPath = $@"{root}\Run_{Guid.NewGuid():N}";
        try
        {
            var service = new WindowsAutostartService(keyPath, "JustDownload", () => @"C:\Apps\JustDownload.exe");

            service.IsSupported.Should().BeTrue();
            service.IsEnabled().Should().BeFalse("nothing is registered yet");

            service.SetEnabled(true);
            service.IsEnabled().Should().BeTrue();
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath))
            {
                key!.GetValue("JustDownload").Should().Be("\"C:\\Apps\\JustDownload.exe\"",
                    "the command is registered, quoted against spaces");
            }

            service.SetEnabled(false);
            service.IsEnabled().Should().BeFalse("disabling removes the entry (reversible)");
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
        }
    }

    private static ISettingsService SettingsWith(bool launchAtStartup)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings { LaunchAtStartup = launchAtStartup });
        return settings;
    }

    [Fact]
    public void Controller_ApplyCurrent_SetsAutostartFromSetting()
    {
        var autostart = Substitute.For<IAutostartService>();
        autostart.IsSupported.Returns(true);
        using var controller = new AutostartController(autostart, SettingsWith(launchAtStartup: true), Substitute.For<JustDownload.Core.IPortableEnvironment>());

        controller.ApplyCurrent();

        autostart.Received(1).SetEnabled(true);
    }

    [Fact]
    public void Controller_WhenUnsupported_DoesNotTouchAutostart()
    {
        var autostart = Substitute.For<IAutostartService>();
        autostart.IsSupported.Returns(false);
        using var controller = new AutostartController(autostart, SettingsWith(launchAtStartup: true), Substitute.For<JustDownload.Core.IPortableEnvironment>());

        controller.ApplyCurrent();

        autostart.DidNotReceive().SetEnabled(Arg.Any<bool>());
    }

    [Fact]
    public void Controller_InPortableMode_NeverTouchesAutostart()
    {
        var autostart = Substitute.For<IAutostartService>();
        autostart.IsSupported.Returns(true);
        var portable = Substitute.For<JustDownload.Core.IPortableEnvironment>();
        portable.IsPortable.Returns(true);
        using var controller = new AutostartController(autostart, SettingsWith(launchAtStartup: true), portable);

        controller.ApplyCurrent();

        autostart.DidNotReceive().SetEnabled(Arg.Any<bool>());
    }

    [Fact]
    public void Controller_ReAppliesOnSettingsChange()
    {
        var autostart = Substitute.For<IAutostartService>();
        autostart.IsSupported.Returns(true);
        var settings = SettingsWith(launchAtStartup: false);
        using var controller = new AutostartController(autostart, settings, Substitute.For<JustDownload.Core.IPortableEnvironment>());

        settings.Changed += Raise.Event<EventHandler<SettingsChangedEventArgs>>(
            settings,
            new SettingsChangedEventArgs(
                new AppSettings(), new AppSettings { LaunchAtStartup = true }, Array.Empty<string>()));

        autostart.Received(1).SetEnabled(true);
    }
}
