using FluentAssertions;
using JustDownload.Core.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JustDownload.Tests.Transport;

/// <summary>
/// <see cref="BrowserUserAgentFormatter"/> (pure string building) and <see cref="BrowserUserAgentDetector"/>'s
/// version-banner parsing: the two OS-independent pieces of the browser-sourced default User-Agent feature.
/// </summary>
public sealed class BrowserUserAgentFormatterTests
{
    [Fact]
    public void Build_Chrome_MatchesRealChromeUserAgentShape()
    {
        BrowserUserAgentFormatter.Build(BrowserKind.Chrome, "124.0.6367.91", "Windows NT 10.0; Win64; x64")
            .Should().Be(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) "
                + "Chrome/124.0.6367.91 Safari/537.36");
    }

    [Fact]
    public void Build_Edge_AppendsEdgToken()
    {
        BrowserUserAgentFormatter.Build(BrowserKind.Edge, "124.0.2478.51", "Windows NT 10.0; Win64; x64")
            .Should().Be(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) "
                + "Chrome/124.0.2478.51 Safari/537.36 Edg/124.0.2478.51");
    }

    [Fact]
    public void Build_Firefox_UsesGeckoShape()
    {
        BrowserUserAgentFormatter.Build(BrowserKind.Firefox, "124.0.1", "X11; Linux x86_64")
            .Should().Be("Mozilla/5.0 (X11; Linux x86_64; rv:124.0.1) Gecko/20100101 Firefox/124.0.1");
    }

    [Fact]
    public void Build_PublicOverload_ProducesANonEmptyBrowserLikeString()
    {
        string userAgent = BrowserUserAgentFormatter.Build(BrowserKind.Chrome, "124.0.6367.91");

        userAgent.Should().StartWith("Mozilla/5.0 (").And.Contain("Chrome/124.0.6367.91");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Build_RejectsMissingVersion(string? version)
    {
        Action act = () => BrowserUserAgentFormatter.Build(BrowserKind.Chrome, version!, "X11; Linux x86_64");

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("Google Chrome 124.0.6367.91", "124.0.6367.91")]
    [InlineData("Google Chrome 124.0.6367.91 \n", "124.0.6367.91")]
    [InlineData("Microsoft Edge 124.0.2478.51", "124.0.2478.51")]
    [InlineData("Mozilla Firefox 124.0.1", "124.0.1")]
    [InlineData("Chromium 124.0.6367.91 Built on Ubuntu, running on Ubuntu 22.04", "124.0.6367.91")]
    public void ParseVersion_ExtractsTheVersionToken(string banner, string expected)
    {
        BrowserUserAgentDetector.ParseVersion(banner).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("command not found")]
    [InlineData("Usage: something --help")]
    public void ParseVersion_ReturnsNull_WhenNoVersionToken(string banner)
    {
        BrowserUserAgentDetector.ParseVersion(banner).Should().BeNull();
    }
}

/// <summary>
/// <see cref="BrowserUserAgentDetector"/> reading real on-disk metadata (no process spawned — see the class
/// summary for why: an earlier draft that shelled out to <c>chrome.exe --version</c> opened a full browser
/// window on a real machine instead of printing a version and exiting).
/// </summary>
public sealed class BrowserUserAgentDetectorTests
{
    [Fact]
    public async Task TryDetectAsync_FindsAnInstalledChromiumBrowser_OnWindows()
    {
        bool chromeOrEdgeInstalled =
            File.Exists(@"C:\Program Files\Google\Chrome\Application\chrome.exe")
            || File.Exists(@"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe")
            || File.Exists(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe")
            || File.Exists(@"C:\Program Files\Microsoft\Edge\Application\msedge.exe");
        if (!OperatingSystem.IsWindows() || !chromeOrEdgeInstalled)
        {
            return; // this machine can't exercise the real-metadata path; the pure ParseVersion tests cover the rest
        }

        var detector = new BrowserUserAgentDetector(NullLogger<BrowserUserAgentDetector>.Instance);

        (BrowserKind Kind, string Version)? result = await detector.TryDetectAsync();

        result.Should().NotBeNull();
        result!.Value.Kind.Should().BeOneOf(BrowserKind.Chrome, BrowserKind.Edge);
        result.Value.Version.Should().MatchRegex(@"^\d+(\.\d+){1,3}$");
    }
}

/// <summary>
/// <see cref="BrowserUserAgentCache"/>: the on-disk TTL cache that lets the app re-probe installed browsers
/// only once a month instead of on every launch.
/// </summary>
public sealed class BrowserUserAgentCacheTests : IDisposable
{
    private readonly string _file =
        Path.Combine(Path.GetTempPath(), "jd-ua-cache-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public async Task TryReadFreshAsync_ReturnsNull_WhenFileDoesNotExist()
    {
        var cache = new BrowserUserAgentCache(_file);

        (await cache.TryReadFreshAsync(TimeSpan.FromDays(30))).Should().BeNull();
    }

    [Fact]
    public async Task WriteAsync_ThenTryReadFreshAsync_RoundTrips_FromAFreshInstance()
    {
        var writer = new BrowserUserAgentCache(_file);
        await writer.WriteAsync("Mozilla/5.0 test-agent");

        var reader = new BrowserUserAgentCache(_file);
        (await reader.TryReadFreshAsync(TimeSpan.FromDays(30))).Should().Be("Mozilla/5.0 test-agent");
    }

    [Fact]
    public async Task TryReadFreshAsync_ReturnsNull_WhenOlderThanMaxAge()
    {
        var cache = new BrowserUserAgentCache(_file);
        await cache.WriteAsync("Mozilla/5.0 test-agent");

        (await cache.TryReadFreshAsync(TimeSpan.Zero)).Should().BeNull("the entry is already older than a zero max age");
    }

    [Fact]
    public async Task TryReadFreshAsync_ToleratesACorruptFile_ByTreatingItAsMissing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        await File.WriteAllTextAsync(_file, "{ not valid json");

        var cache = new BrowserUserAgentCache(_file);

        (await cache.TryReadFreshAsync(TimeSpan.FromDays(30))).Should().BeNull();
    }

    public void Dispose()
    {
        if (File.Exists(_file))
        {
            File.Delete(_file);
        }
    }
}
