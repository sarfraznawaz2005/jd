using FluentAssertions;
using JustDownload.Core.Abstractions;
using JustDownload.Core.Media.YtDlp;
using JustDownload.Core.Security;
using JustDownload.Core.Settings;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.Media;

/// <summary>
/// Tests for <see cref="IYouTubeSessionStore"/> (the "Sign in to YouTube" session, Windows-only feature):
/// the cookies are stored via <see cref="ISecretStore"/> (never plaintext except the materialized file),
/// and the existing <see cref="AppSettings.YtDlpCookieFilePath"/>/<c>--cookies</c> path is what points at
/// that file — so <c>YtDlpMediaExtractor</c> needs zero changes to consume it.
/// </summary>
public sealed class YouTubeSessionStoreTests : IDisposable
{
    private static readonly NetscapeCookieRecord[] SampleCookies =
    [
        new(".youtube.com", true, "/", true, 100, "SID", "abc"),
    ];

    private readonly string _appName = "jd-session-tests-" + Guid.NewGuid().ToString("N");
    private readonly string _cookieFilePath;

    public YouTubeSessionStoreTests()
    {
        _cookieFilePath = Path.Combine(Path.GetTempPath(), _appName, "youtube-session-cookies.txt");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.Combine(Path.GetTempPath(), _appName), recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static ISettingsService SettingsWith(AppSettings current)
    {
        var settings = Substitute.For<ISettingsService>();
        AppSettings snapshot = current;
        settings.Current.Returns(_ => snapshot);
        settings.UpdateAsync(Arg.Any<Func<AppSettings, AppSettings>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                snapshot = ci.Arg<Func<AppSettings, AppSettings>>()(snapshot);
                return Task.FromResult(snapshot);
            });
        return settings;
    }

    private IAppInfoProvider AppInfo()
    {
        var appInfo = Substitute.For<IAppInfoProvider>();
        appInfo.Name.Returns(_appName);
        return appInfo;
    }

    [Fact]
    public void HasSession_ReflectsSettingsSnapshot()
    {
        var withSession = new YouTubeSessionStore(
            SettingsWith(new AppSettings { YouTubeSessionSecretRef = "ref-1" }), Substitute.For<ISecretStore>(), AppInfo());
        var withoutSession = new YouTubeSessionStore(
            SettingsWith(new AppSettings()), Substitute.For<ISecretStore>(), AppInfo());

        withSession.HasSession.Should().BeTrue();
        withoutSession.HasSession.Should().BeFalse();
    }

    [Fact]
    public async Task StoreAsync_WritesSecretAndCookieFile_AndPointsYtDlpCookieFilePathAtIt()
    {
        var secrets = Substitute.For<ISecretStore>();
        secrets.StoreAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("new-ref");
        ISettingsService settings = SettingsWith(new AppSettings());
        var store = new YouTubeSessionStore(settings, secrets, AppInfo());

        await store.StoreAsync(SampleCookies);

        await secrets.Received(1).StoreAsync(
            Arg.Is<string>(s => s.Contains("SID") && s.Contains("abc")), Arg.Any<CancellationToken>());
        File.Exists(_cookieFilePath).Should().BeTrue();
        (await File.ReadAllTextAsync(_cookieFilePath)).Should().Contain("SID");
        settings.Current.YouTubeSessionSecretRef.Should().Be("new-ref");
        settings.Current.YtDlpCookieFilePath.Should().Be(_cookieFilePath);
    }

    [Fact]
    public async Task StoreAsync_ReplacingExistingSession_DeletesThePreviousSecret()
    {
        var secrets = Substitute.For<ISecretStore>();
        secrets.StoreAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("new-ref");
        ISettingsService settings = SettingsWith(new AppSettings { YouTubeSessionSecretRef = "old-ref" });
        var store = new YouTubeSessionStore(settings, secrets, AppInfo());

        await store.StoreAsync(SampleCookies);

        await secrets.Received(1).DeleteAsync("old-ref", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClearAsync_DeletesSecretAndFile_AndClearsBothSettingsFields()
    {
        var secrets = Substitute.For<ISecretStore>();
        secrets.StoreAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("ref-1");
        ISettingsService settings = SettingsWith(new AppSettings());
        var store = new YouTubeSessionStore(settings, secrets, AppInfo());
        await store.StoreAsync(SampleCookies);
        File.Exists(_cookieFilePath).Should().BeTrue("StoreAsync must have materialized it first");

        await store.ClearAsync();

        await secrets.Received(1).DeleteAsync("ref-1", Arg.Any<CancellationToken>());
        File.Exists(_cookieFilePath).Should().BeFalse();
        settings.Current.YouTubeSessionSecretRef.Should().BeNull();
        settings.Current.YtDlpCookieFilePath.Should().BeNull();
        store.HasSession.Should().BeFalse();
    }

    [Fact]
    public async Task ClearAsync_NoSession_IsANoOp()
    {
        var secrets = Substitute.For<ISecretStore>();
        var store = new YouTubeSessionStore(SettingsWith(new AppSettings()), secrets, AppInfo());

        await store.ClearAsync();

        await secrets.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureMaterializedAsync_SessionStoredButFileMissing_RewritesItFromTheVault()
    {
        var secrets = Substitute.For<ISecretStore>();
        secrets.RetrieveAsync("ref-1", Arg.Any<CancellationToken>()).Returns("# Netscape HTTP Cookie File\n\nSID\n");
        var store = new YouTubeSessionStore(
            SettingsWith(new AppSettings { YouTubeSessionSecretRef = "ref-1" }), secrets, AppInfo());
        File.Exists(_cookieFilePath).Should().BeFalse("nothing has materialized it yet in this test");

        await store.EnsureMaterializedAsync();

        File.Exists(_cookieFilePath).Should().BeTrue();
        (await File.ReadAllTextAsync(_cookieFilePath)).Should().Contain("SID");
    }

    [Fact]
    public async Task EnsureMaterializedAsync_FileAlreadyPresent_DoesNotTouchTheVault()
    {
        var secrets = Substitute.For<ISecretStore>();
        var store = new YouTubeSessionStore(
            SettingsWith(new AppSettings { YouTubeSessionSecretRef = "ref-1" }), secrets, AppInfo());
        Directory.CreateDirectory(Path.GetDirectoryName(_cookieFilePath)!);
        await File.WriteAllTextAsync(_cookieFilePath, "already here");

        await store.EnsureMaterializedAsync();

        await secrets.DidNotReceive().RetrieveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureMaterializedAsync_VaultEntryGone_ClearsTheDanglingSession()
    {
        var secrets = Substitute.For<ISecretStore>();
        secrets.RetrieveAsync("ref-1", Arg.Any<CancellationToken>()).Returns((string?)null);
        ISettingsService settings = SettingsWith(new AppSettings { YouTubeSessionSecretRef = "ref-1" });
        var store = new YouTubeSessionStore(settings, secrets, AppInfo());

        await store.EnsureMaterializedAsync();

        settings.Current.YouTubeSessionSecretRef.Should().BeNull();
        File.Exists(_cookieFilePath).Should().BeFalse();
    }

    [Fact]
    public async Task EnsureMaterializedAsync_NoSession_IsANoOp()
    {
        var secrets = Substitute.For<ISecretStore>();
        var store = new YouTubeSessionStore(SettingsWith(new AppSettings()), secrets, AppInfo());

        await store.EnsureMaterializedAsync();

        await secrets.DidNotReceive().RetrieveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        File.Exists(_cookieFilePath).Should().BeFalse();
    }
}
