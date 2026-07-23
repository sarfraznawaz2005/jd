using FluentAssertions;
using JustDownload.Core.Settings;
using JustDownload.Core.Transport.Proxy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JustDownload.Tests.Settings;

/// <summary>
/// Round-trip tests for the remembered New Download dialog choices (TASK-227) and the progress-window
/// preferences (TASK-225), including the defensive fallbacks on absent or corrupt stored values.
/// </summary>
public sealed class SettingsSerializerNewDownloadTests
{
    private static AppSettings RoundTrip(AppSettings settings)
    {
        IReadOnlyDictionary<string, string> stored = SettingsSerializer.ToStorage(settings);
        return SettingsSerializer.FromStorage(
            stored.ToDictionary(kv => kv.Key, kv => (string?)kv.Value), NullLogger.Instance);
    }

    [Fact]
    public void RoundTrips_EveryRememberedChoice()
    {
        var settings = new AppSettings
        {
            NewDownloadFolder = @"C:\Users\me\Downloads\Video",
            NewDownloadCategory = "Video",
            NewDownloadUseSegmentation = false,
            NewDownloadUseProxyOverride = true,
            NewDownloadProxyKind = ProxyKind.Socks5,
            NewDownloadProxyHost = "proxy.example",
            NewDownloadProxyPort = 1080,
            NewDownloadProxyUsername = "me",
            NewDownloadProxyDomain = "CORP",
            NewDownloadProxyPasswordSecretRef = "secret-ref-123",
            NewDownloadUseAlternateUrls = true,
        };

        AppSettings restored = RoundTrip(settings);

        restored.NewDownloadFolder.Should().Be(@"C:\Users\me\Downloads\Video");
        restored.NewDownloadCategory.Should().Be("Video");
        restored.NewDownloadUseSegmentation.Should().BeFalse();
        restored.NewDownloadUseProxyOverride.Should().BeTrue();
        restored.NewDownloadProxyKind.Should().Be(ProxyKind.Socks5);
        restored.NewDownloadProxyHost.Should().Be("proxy.example");
        restored.NewDownloadProxyPort.Should().Be(1080);
        restored.NewDownloadProxyUsername.Should().Be("me");
        restored.NewDownloadProxyDomain.Should().Be("CORP");
        restored.NewDownloadProxyPasswordSecretRef.Should().Be("secret-ref-123");
        restored.NewDownloadUseAlternateUrls.Should().BeTrue();
    }

    [Fact]
    public void RoundTrips_ProgressWindowPreferences()
    {
        AppSettings restored = RoundTrip(new AppSettings
        {
            ShowDownloadProgressWindow = false,
            CloseProgressWindowWhenDone = true,
        });

        restored.ShowDownloadProgressWindow.Should().BeFalse();
        restored.CloseProgressWindowWhenDone.Should().BeTrue();
    }

    [Fact]
    public void EmptyStorage_YieldsTheDocumentedDefaults()
    {
        AppSettings restored =
            SettingsSerializer.FromStorage(new Dictionary<string, string?>(), NullLogger.Instance);

        restored.NewDownloadFolder.Should().BeNull("an untouched folder must keep auto-detecting");
        restored.NewDownloadCategory.Should().BeNull("no remembered category means Auto-detect");
        restored.NewDownloadUseSegmentation.Should().BeTrue();
        restored.NewDownloadUseProxyOverride.Should().BeFalse();
        restored.NewDownloadUseAlternateUrls.Should().BeFalse();
        restored.ShowDownloadProgressWindow.Should().BeTrue();
        restored.CloseProgressWindowWhenDone.Should().BeFalse();
    }

    [Fact]
    public void CorruptValues_FallBackToDefaults_WithoutThrowing()
    {
        var stored = new Dictionary<string, string?>
        {
            [SettingsSerializer.NewDownloadUseSegmentationKey] = "not-a-bool",
            [SettingsSerializer.NewDownloadProxyKindKey] = "Carrier-Pigeon",
            [SettingsSerializer.NewDownloadProxyPortKey] = "eighty",
            [SettingsSerializer.ShowDownloadProgressWindowKey] = "maybe",
        };

        AppSettings restored = SettingsSerializer.FromStorage(stored, NullLogger.Instance);

        restored.NewDownloadUseSegmentation.Should().BeTrue();
        restored.NewDownloadProxyKind.Should().Be(ProxyKind.Http);
        restored.NewDownloadProxyPort.Should().Be(0);
        restored.ShowDownloadProgressWindow.Should().BeTrue();
    }
}
