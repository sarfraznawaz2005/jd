using FluentAssertions;
using JustDownload.Core.Settings;
using JustDownload.Core.Transport.Proxy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JustDownload.Tests.Settings;

/// <summary>
/// Portable settings backup/restore (TASK-129): a JSON export round-trips every preference, the machine-local
/// proxy password reference never travels, and a malformed file fails loudly rather than silently.
/// </summary>
public sealed class SettingsTransferTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "jd-settings-" + Guid.NewGuid().ToString("N") + ".json");

    private readonly SettingsTransfer _transfer = new(NullLogger<SettingsTransfer>.Instance);

    private static AppSettings NonDefault() => new()
    {
        MaxConcurrentDownloads = 7,
        ConnectionsPerDownload = 12,
        MaxDownloadRetries = 9,
        GlobalSpeedLimitBytesPerSecond = 1_048_576,
        DefaultVideoQuality = VideoQuality.P1440,
        DefaultContainer = MediaContainer.Mp4,
        Density = UiDensity.Compact,
        Theme = AppTheme.Dark,
        DefaultDownloadDirectory = "downloads-dir",
        OrganizeByCategory = true,
        OrganizedRootDirectory = "organized-root",
        StartMinimizedToTray = true,
        CloseToTray = true,
        MonitorClipboard = true,
        LaunchAtStartup = true,
        NotificationsEnabled = false,
        AutoExtractArchives = true,
        ProxyKind = ProxyKind.Http,
        ProxyHost = "proxy.local",
        ProxyPort = 8080,
        ProxyUsername = "user",
        ProxyDomain = "CORP",
    };

    [Fact]
    public async Task ExportThenImport_RestoresEveryPreference()
    {
        AppSettings original = NonDefault();

        await _transfer.ExportAsync(original, _path);
        AppSettings restored = await _transfer.ImportAsync(_path);

        restored.Should().Be(original);
    }

    [Fact]
    public async Task Export_ExcludesTheProxyPasswordSecretRef()
    {
        AppSettings withSecret = NonDefault() with { ProxyPasswordSecretRef = "jd-proxy-pwd" };

        await _transfer.ExportAsync(withSecret, _path);
        string json = await File.ReadAllTextAsync(_path);
        AppSettings restored = await _transfer.ImportAsync(_path);

        json.Should().NotContain("jd-proxy-pwd", "the keychain reference is machine-local and must not travel");
        restored.ProxyPasswordSecretRef.Should().BeNull();
    }

    [Fact]
    public async Task Import_RejectsAFileThatIsNotAValidExport()
    {
        await File.WriteAllTextAsync(_path, "this is not json");

        Func<Task> act = () => _transfer.ImportAsync(_path);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Import_RejectsJsonWithoutValues()
    {
        await File.WriteAllTextAsync(_path, "{\"schema\":1}");

        Func<Task> act = () => _transfer.ImportAsync(_path);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (IOException)
        {
        }
    }
}
