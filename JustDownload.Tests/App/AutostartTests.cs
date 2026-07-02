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

    [Fact]
    public void MacOsAutostart_SetEnabled_WritesThenRemoves_AnIsolatedPlist()
    {
        // Pure file I/O (no P/Invoke), so this round-trips for real on any OS, including this Windows box.
        string directory = Path.Combine(Path.GetTempPath(), $"JustDownloadTests_LaunchAgents_{Guid.NewGuid():N}");
        try
        {
            var service = new MacOsAutostartService(directory, "com.justdownload.test", () => "/Applications/JustDownload.app/Contents/MacOS/JustDownload");

            service.IsEnabled().Should().BeFalse("nothing is registered yet");

            service.SetEnabled(true);

            string plistPath = Path.Combine(directory, "com.justdownload.test.plist");
            File.Exists(plistPath).Should().BeTrue("enabling writes the LaunchAgent plist");
            service.IsEnabled().Should().BeTrue();

            string content = File.ReadAllText(plistPath);
            content.Should().Contain("<key>Label</key>").And.Contain("<string>com.justdownload.test</string>",
                "the Label must match the filename stem, which macOS requires");
            content.Should().Contain("<string>/Applications/JustDownload.app/Contents/MacOS/JustDownload</string>",
                "ProgramArguments carries the registered command");
            content.Should().Contain("<key>RunAtLoad</key>").And.Contain("<true/>");

            service.SetEnabled(false);
            service.IsEnabled().Should().BeFalse("disabling removes the entry (reversible)");
            File.Exists(plistPath).Should().BeFalse();

            service.SetEnabled(false); // idempotent — no throw when already absent
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void MacOsAutostart_SetEnabled_CreatesTargetDirectory_WhenMissing()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"JustDownloadTests_LaunchAgents_{Guid.NewGuid():N}");
        try
        {
            Directory.Exists(directory).Should().BeFalse();
            var service = new MacOsAutostartService(directory, "com.justdownload.test", () => "/usr/local/bin/justdownload");

            service.SetEnabled(true);

            Directory.Exists(directory).Should().BeTrue("the LaunchAgents directory is created on demand");
            service.IsEnabled().Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void MacOsAutostart_IsSupported_OnlyOnMacOs()
    {
        new MacOsAutostartService().IsSupported.Should().Be(OperatingSystem.IsMacOS());
    }

    [Fact]
    public void LinuxAutostart_SetEnabled_WritesThenRemoves_AnIsolatedDesktopEntry()
    {
        // Pure file I/O (no P/Invoke), so this round-trips for real on any OS, including this Windows box.
        string directory = Path.Combine(Path.GetTempPath(), $"JustDownloadTests_autostart_{Guid.NewGuid():N}");
        try
        {
            var service = new LinuxAutostartService(directory, "justdownload-test", () => "/usr/bin/justdownload");

            service.IsEnabled().Should().BeFalse("nothing is registered yet");

            service.SetEnabled(true);

            string desktopPath = Path.Combine(directory, "justdownload-test.desktop");
            File.Exists(desktopPath).Should().BeTrue("enabling writes the XDG autostart entry");
            service.IsEnabled().Should().BeTrue();

            string content = File.ReadAllText(desktopPath);
            content.Should().Contain("[Desktop Entry]")
                .And.Contain("Type=Application")
                .And.Contain("Exec=/usr/bin/justdownload", "Exec carries the registered command")
                .And.Contain("X-GNOME-Autostart-enabled=true")
                .And.Contain("Hidden=false");

            service.SetEnabled(false);
            service.IsEnabled().Should().BeFalse("disabling removes the entry (reversible)");
            File.Exists(desktopPath).Should().BeFalse();

            service.SetEnabled(false); // idempotent — no throw when already absent
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void LinuxAutostart_SetEnabled_QuotesCommand_WhenItContainsSpaces()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"JustDownloadTests_autostart_{Guid.NewGuid():N}");
        try
        {
            var service = new LinuxAutostartService(directory, "justdownload-test", () => "/opt/Just Download/justdownload");

            service.SetEnabled(true);

            string content = File.ReadAllText(Path.Combine(directory, "justdownload-test.desktop"));
            content.Should().Contain("Exec=\"/opt/Just Download/justdownload\"");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void LinuxAutostart_SetEnabled_CreatesTargetDirectory_WhenMissing()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"JustDownloadTests_autostart_{Guid.NewGuid():N}");
        try
        {
            Directory.Exists(directory).Should().BeFalse();
            var service = new LinuxAutostartService(directory, "justdownload-test", () => "/usr/bin/justdownload");

            service.SetEnabled(true);

            Directory.Exists(directory).Should().BeTrue("the autostart directory is created on demand");
            service.IsEnabled().Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void LinuxAutostart_IsSupported_OnlyOnLinux()
    {
        new LinuxAutostartService().IsSupported.Should().Be(OperatingSystem.IsLinux());
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
