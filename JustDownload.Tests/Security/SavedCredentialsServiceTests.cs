using FluentAssertions;
using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.Security;
using JustDownload.Core.Settings;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.Security;

/// <summary>
/// View/remove saved credentials (TASK-126): the service enumerates the app-tracked keychain references — the
/// global proxy password and per-download cookie/proxy secrets — and revoking one deletes the keychain entry
/// and clears its reference. It never reads or exposes the secret value (§5).
/// </summary>
public sealed class SavedCredentialsServiceTests
{
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IDownloadRepository _downloads = Substitute.For<IDownloadRepository>();
    private readonly ISecretStore _secrets = Substitute.For<ISecretStore>();

    private SavedCredentialsService Service() => new(_settings, _downloads, _secrets);

    [Fact]
    public async Task ListAsync_ReturnsGlobalProxyAndPerDownloadSecrets()
    {
        _settings.Current.Returns(new AppSettings
        {
            ProxyHost = "proxy.local",
            ProxyPasswordSecretRef = "ref-global",
        });
        _downloads.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            new Download
            {
                Id = 7, Url = "https://site.example/a.bin", Status = "complete",
                ProxyPasswordSecretRef = "ref-dl-proxy", CookieSecretRef = "ref-dl-cookie",
            },
            new Download { Id = 8, Url = "https://x/y", Status = "complete" }, // no secrets — excluded
        });

        IReadOnlyList<SavedCredential> list = await Service().ListAsync();

        list.Should().HaveCount(3);
        list.Should().Contain(c => c.Kind == SavedCredentialKind.GlobalProxyPassword && c.DownloadId == null);
        list.Should().Contain(c => c.Kind == SavedCredentialKind.DownloadProxyPassword && c.DownloadId == 7);
        list.Should().Contain(c => c.Kind == SavedCredentialKind.DownloadCookies && c.DownloadId == 7);
        list.Should().NotContain(c => c.Description.Contains("ref-", StringComparison.Ordinal),
            "descriptions never leak the secret reference or value");
    }

    [Fact]
    public async Task RemoveAsync_GlobalProxy_DeletesSecret_AndClearsTheSettingRef()
    {
        _settings.Current.Returns(new AppSettings { ProxyPasswordSecretRef = "ref-global" });

        await Service().RemoveAsync(
            new SavedCredential(SavedCredentialKind.GlobalProxyPassword, "Proxy password", null));

        await _secrets.Received(1).DeleteAsync("ref-global", Arg.Any<CancellationToken>());
        await _settings.Received(1).UpdateAsync(Arg.Any<Func<AppSettings, AppSettings>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveAsync_DownloadCookies_DeletesSecret_AndClearsTheDownloadRef()
    {
        var download = new Download
        {
            Id = 7,
            Url = "https://site/a.bin",
            Status = "complete",
            CookieSecretRef = "ref-dl-cookie",
        };
        _downloads.GetAsync(7, Arg.Any<CancellationToken>()).Returns(download);

        await Service().RemoveAsync(
            new SavedCredential(SavedCredentialKind.DownloadCookies, "Cookies for download", 7));

        await _secrets.Received(1).DeleteAsync("ref-dl-cookie", Arg.Any<CancellationToken>());
        await _downloads.Received(1).UpdateAsync(
            Arg.Is<Download>(d => d.Id == 7 && d.CookieSecretRef == null), Arg.Any<CancellationToken>());
    }
}
