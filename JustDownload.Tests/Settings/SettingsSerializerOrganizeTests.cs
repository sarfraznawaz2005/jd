using FluentAssertions;
using JustDownload.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JustDownload.Tests.Settings;

/// <summary>Round-trip tests for the organize-by-category settings added in TASK-046.</summary>
public sealed class SettingsSerializerOrganizeTests
{
    [Fact]
    public void RoundTrips_OrganizeSettings()
    {
        var settings = new AppSettings
        {
            OrganizeByCategory = true,
            OrganizedRootDirectory = @"C:\Sorted",
        };

        IReadOnlyDictionary<string, string> stored = SettingsSerializer.ToStorage(settings);
        AppSettings restored = SettingsSerializer.FromStorage(
            stored.ToDictionary(kv => kv.Key, kv => (string?)kv.Value),
            NullLogger.Instance);

        restored.OrganizeByCategory.Should().BeTrue();
        restored.OrganizedRootDirectory.Should().Be(@"C:\Sorted");
    }

    [Fact]
    public void DefaultsAreOff_WhenStorageEmpty()
    {
        AppSettings restored = SettingsSerializer.FromStorage(
            new Dictionary<string, string?>(), NullLogger.Instance);

        restored.OrganizeByCategory.Should().BeFalse();
        restored.OrganizedRootDirectory.Should().BeNull();
    }

    [Fact]
    public void EmptyRoot_DeserializesAsNull()
    {
        var stored = new Dictionary<string, string?>
        {
            [SettingsSerializer.OrganizedRootDirectoryKey] = string.Empty,
        };

        SettingsSerializer.FromStorage(stored, NullLogger.Instance).OrganizedRootDirectory.Should().BeNull();
    }

    [Fact]
    public void RoundTrips_TraySettings()
    {
        var settings = new AppSettings { StartMinimizedToTray = true, CloseToTray = true };

        IReadOnlyDictionary<string, string> stored = SettingsSerializer.ToStorage(settings);
        AppSettings restored = SettingsSerializer.FromStorage(
            stored.ToDictionary(kv => kv.Key, kv => (string?)kv.Value),
            NullLogger.Instance);

        restored.StartMinimizedToTray.Should().BeTrue();
        restored.CloseToTray.Should().BeTrue();
    }

    [Fact]
    public void TrayDefaultsAreOff_WhenStorageEmpty()
    {
        AppSettings restored = SettingsSerializer.FromStorage(
            new Dictionary<string, string?>(), NullLogger.Instance);

        restored.StartMinimizedToTray.Should().BeFalse();
        restored.CloseToTray.Should().BeFalse();
    }

    [Fact]
    public void RoundTrips_DefaultDownloadDirectory()
    {
        var settings = new AppSettings { DefaultDownloadDirectory = @"D:\Saved" };

        IReadOnlyDictionary<string, string> stored = SettingsSerializer.ToStorage(settings);
        AppSettings restored = SettingsSerializer.FromStorage(
            stored.ToDictionary(kv => kv.Key, kv => (string?)kv.Value),
            NullLogger.Instance);

        restored.DefaultDownloadDirectory.Should().Be(@"D:\Saved");
    }

    [Fact]
    public void EmptyDefaultDownloadDirectory_DeserializesAsNull()
    {
        var stored = new Dictionary<string, string?>
        {
            [SettingsSerializer.DefaultDownloadDirectoryKey] = string.Empty,
        };

        SettingsSerializer.FromStorage(stored, NullLogger.Instance).DefaultDownloadDirectory.Should().BeNull();
    }

    [Fact]
    public void RoundTrips_ProxySettings()
    {
        var settings = new AppSettings
        {
            ProxyKind = JustDownload.Core.Transport.Proxy.ProxyKind.Socks5,
            ProxyHost = "proxy.local",
            ProxyPort = 1080,
            ProxyUsername = "user",
            ProxyDomain = "CORP",
            ProxyPasswordSecretRef = "ref-1",
        };

        IReadOnlyDictionary<string, string> stored = SettingsSerializer.ToStorage(settings);
        AppSettings restored = SettingsSerializer.FromStorage(
            stored.ToDictionary(kv => kv.Key, kv => (string?)kv.Value),
            NullLogger.Instance);

        restored.ProxyKind.Should().Be(JustDownload.Core.Transport.Proxy.ProxyKind.Socks5);
        restored.ProxyHost.Should().Be("proxy.local");
        restored.ProxyPort.Should().Be(1080);
        restored.ProxyUsername.Should().Be("user");
        restored.ProxyDomain.Should().Be("CORP");
        restored.ProxyPasswordSecretRef.Should().Be("ref-1");
    }

    [Fact]
    public void ProxyDefaults_WhenStorageEmpty()
    {
        AppSettings restored = SettingsSerializer.FromStorage(
            new Dictionary<string, string?>(), NullLogger.Instance);

        restored.ProxyKind.Should().Be(JustDownload.Core.Transport.Proxy.ProxyKind.None);
        restored.ProxyHost.Should().BeNull();
        restored.ProxyPasswordSecretRef.Should().BeNull();
    }

    [Fact]
    public void RoundTrips_MonitorClipboard_AndDefaultsOff()
    {
        IReadOnlyDictionary<string, string> stored =
            SettingsSerializer.ToStorage(new AppSettings { MonitorClipboard = true });
        SettingsSerializer.FromStorage(
            stored.ToDictionary(kv => kv.Key, kv => (string?)kv.Value), NullLogger.Instance)
            .MonitorClipboard.Should().BeTrue();

        SettingsSerializer.FromStorage(new Dictionary<string, string?>(), NullLogger.Instance)
            .MonitorClipboard.Should().BeFalse();
    }

    [Fact]
    public void RoundTrips_LaunchAtStartup_AndDefaultsOff()
    {
        IReadOnlyDictionary<string, string> stored =
            SettingsSerializer.ToStorage(new AppSettings { LaunchAtStartup = true });
        SettingsSerializer.FromStorage(
            stored.ToDictionary(kv => kv.Key, kv => (string?)kv.Value), NullLogger.Instance)
            .LaunchAtStartup.Should().BeTrue();

        SettingsSerializer.FromStorage(new Dictionary<string, string?>(), NullLogger.Instance)
            .LaunchAtStartup.Should().BeFalse();
    }
}
