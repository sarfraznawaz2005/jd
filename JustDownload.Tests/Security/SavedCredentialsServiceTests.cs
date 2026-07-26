using FluentAssertions;
using JustDownload.Core.Abstractions;
using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.Lifecycle;
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
    private static readonly DateTimeOffset Now = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IDownloadRepository _downloads = Substitute.For<IDownloadRepository>();
    private readonly ISecretStore _secrets = Substitute.For<ISecretStore>();
    private readonly IClock _clock = Substitute.For<IClock>();

    private SavedCredentialsService Service()
    {
        _clock.UtcNow.Returns(Now);
        return new SavedCredentialsService(_settings, _downloads, _secrets, _clock);
    }

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

    /// <summary>
    /// A description has to identify which download it belongs to. Host-only labels made every credential
    /// from one site read identically (user-reported: fifteen indistinguishable "github.com" rows), leaving
    /// no way to choose what to revoke. The file name supplies the identity; the URL still goes through
    /// SafeLogUrl, so the path, query string and any token stay out of it (§5).
    /// </summary>
    [Fact]
    public async Task ListAsync_NamesTheDownloadByFileName_WithoutLeakingTheUrlsQueryString()
    {
        _settings.Current.Returns(new AppSettings());
        _downloads.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            new Download
            {
                Id = 1, Url = "https://github.com/o/r/releases/download/v1/Setup.exe?token=SECRET&sig=ABC",
                Filename = "Setup.exe", Status = "complete", CookieSecretRef = "ref-a",
            },
            new Download
            {
                Id = 2, Url = "https://github.com/o/r/releases/download/v2/Other.msi?token=SECRET",
                Filename = "Other.msi", Status = "complete", CookieSecretRef = "ref-b",
            },
        });

        IReadOnlyList<SavedCredential> list = await Service().ListAsync();

        list.Select(c => c.Description).Should().Equal(
            "Cookies for Setup.exe — https://github.com",
            "Cookies for Other.msi — https://github.com");
        list.Should().OnlyContain(c => !c.Description.Contains("SECRET", StringComparison.Ordinal)
            && !c.Description.Contains("sig=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ListAsync_FallsBackToTheOrigin_WhenTheDownloadHasNoFileNameYet()
    {
        _settings.Current.Returns(new AppSettings());
        _downloads.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            new Download { Id = 1, Url = "https://site.example/a", Status = "queued", CookieSecretRef = "ref-a" },
        });

        IReadOnlyList<SavedCredential> list = await Service().ListAsync();

        list.Single().Description.Should().Be("Cookies for download https://site.example");
    }

    private static Download Recent(long id, string status, string? cookieRef, TimeSpan? age = null) => new()
    {
        Id = id,
        Url = $"https://s/{id}",
        Status = status,
        CookieSecretRef = cookieRef,
        CreatedAt = Now - (age ?? TimeSpan.Zero),
    };

    /// <summary>
    /// Cookies captured for a download that has already finished are dead weight in the keychain — the
    /// engine now deletes them at completion, and this clears the ones saved before it did. A recent
    /// unfinished download keeps its own: every non-completed state is resumable and resume re-sends the
    /// Cookie header.
    /// </summary>
    [Fact]
    public async Task PurgeStaleDownloadCookies_DeletesFinishedDownloadsCookies_AndKeepsResumableOnes()
    {
        _downloads.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            Recent(1, DownloadStatusCodes.Completed, "ref-done"),
            Recent(2, DownloadStatusCodes.Paused, "ref-paused"),
            Recent(3, DownloadStatusCodes.Failed, "ref-failed"),
            Recent(4, DownloadStatusCodes.Completed, cookieRef: null), // no cookies
        });

        int purged = await Service().PurgeStaleDownloadCookiesAsync();

        purged.Should().Be(1);
        await _secrets.Received(1).DeleteAsync("ref-done", Arg.Any<CancellationToken>());
        await _secrets.DidNotReceive().DeleteAsync("ref-paused", Arg.Any<CancellationToken>());
        await _secrets.DidNotReceive().DeleteAsync("ref-failed", Arg.Any<CancellationToken>());
        await _downloads.Received(1).UpdateAsync(
            Arg.Is<Download>(d => d.Id == 1 && d.CookieSecretRef == null), Arg.Any<CancellationToken>());
        await _downloads.DidNotReceive().UpdateAsync(
            Arg.Is<Download>(d => d.Id != 1), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Completion is not the only way cookies stop being useful: a download that is never started, never
    /// resumed and never deleted would hold its cookies forever (user-reported: a queued item months old
    /// still listed in Settings > Authentication). Cookies are captured once at enqueue and never refreshed,
    /// so the record's age is the cookies' age — past the max age they go, whatever state the download is in.
    /// </summary>
    [Theory]
    [InlineData(DownloadStatusCodes.Queued)]
    [InlineData(DownloadStatusCodes.Paused)]
    [InlineData(DownloadStatusCodes.Failed)]
    [InlineData(DownloadStatusCodes.Expired)]
    public async Task PurgeStaleDownloadCookies_DropsCookiesOlderThanTheMaxAge_InAnyState(string status)
    {
        _downloads.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            Recent(1, status, "ref-old", SavedCredentialsService.CookieMaxAge + TimeSpan.FromDays(1)),
        });

        int purged = await Service().PurgeStaleDownloadCookiesAsync();

        purged.Should().Be(1);
        await _secrets.Received(1).DeleteAsync("ref-old", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PurgeStaleDownloadCookies_KeepsCookiesStillWithinTheMaxAge()
    {
        _downloads.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            Recent(1, DownloadStatusCodes.Queued, "ref-fresh", SavedCredentialsService.CookieMaxAge - TimeSpan.FromDays(1)),
        });

        int purged = await Service().PurgeStaleDownloadCookiesAsync();

        purged.Should().Be(0);
        await _secrets.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PurgeStaleDownloadCookies_IsIdempotent()
    {
        _downloads.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            Recent(1, DownloadStatusCodes.Completed, cookieRef: null),
        });

        (await Service().PurgeStaleDownloadCookiesAsync()).Should().Be(0);
        await _secrets.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
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
