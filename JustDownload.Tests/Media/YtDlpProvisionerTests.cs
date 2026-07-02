using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using JustDownload.Core.Abstractions;
using JustDownload.Core.Integrity;
using JustDownload.Core.Media;
using JustDownload.Core.Transport;
using JustDownload.Tests.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace JustDownload.Tests.Media;

/// <summary>
/// Download-on-first-use provisioning of the pinned yt-dlp release (TASK-162, D3). Drives the provisioner
/// against a loopback server serving a fake yt-dlp executable so the download → SHA-256 verify → move flow
/// is tested offline and deterministically, plus integrity rejection, idempotence, and the graceful
/// "no source for this platform" path.
/// </summary>
public sealed class YtDlpProvisionerTests : IDisposable
{
    private static readonly string ExecutableName = OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp";

    private readonly string _vendorDir = Path.Combine(
        Path.GetTempPath(), "jd-ytdlp-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_vendorDir))
            {
                Directory.Delete(_vendorDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task EnsureAsync_DownloadsVerifiesAndMoves_WhenAbsent()
    {
        (byte[] binary, string sha256) = FakeYtDlpBinary();
        await using var server = new LoopbackHttpServer { Body = binary, ContentType = "application/octet-stream" };
        var manifest = ManifestFor(server.Url("yt-dlp"), sha256);

        string exePath = Path.Combine(_vendorDir, ExecutableName);
        IYtDlpLocator locator = Substitute.For<IYtDlpLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(
            _ => File.Exists(exePath) ? new YtDlpInfo(exePath, "9.9.9") : null);

        YtDlpProvisioner provisioner = CreateProvisioner(manifest, locator);

        YtDlpInfo? result = await provisioner.EnsureAsync();

        result.Should().NotBeNull();
        result!.ExecutablePath.Should().Be(exePath);
        File.Exists(exePath).Should().BeTrue("the binary is moved into the vendor directory");
        File.Exists(Path.Combine(_vendorDir, ".yt-dlp-download.partial"))
            .Should().BeFalse("the temp download is removed after the move");
    }

    [Fact]
    public async Task EnsureAsync_Throws_AndLeavesNothing_OnChecksumMismatch()
    {
        (byte[] binary, _) = FakeYtDlpBinary();
        await using var server = new LoopbackHttpServer { Body = binary };
        var manifest = ManifestFor(server.Url("yt-dlp"), new string('a', 64));

        IYtDlpLocator locator = Substitute.For<IYtDlpLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns((YtDlpInfo?)null);

        YtDlpProvisioner provisioner = CreateProvisioner(manifest, locator);

        Func<Task> act = async () => await provisioner.EnsureAsync();

        await act.Should().ThrowAsync<YtDlpException>().WithMessage("*integrity check*");
        File.Exists(Path.Combine(_vendorDir, ExecutableName))
            .Should().BeFalse("a corrupt download must not be kept");
        File.Exists(Path.Combine(_vendorDir, ".yt-dlp-download.partial"))
            .Should().BeFalse("the rejected temp download is cleaned up");
    }

    [Fact]
    public async Task EnsureAsync_ReturnsExisting_WithoutDownloading()
    {
        var existing = new YtDlpInfo("/usr/bin/yt-dlp", "2026.06.09");
        IYtDlpLocator locator = Substitute.For<IYtDlpLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(existing);

        // An unreachable URL would fail if a download were attempted; it must not be.
        var manifest = ManifestFor(new Uri("http://127.0.0.1:1/never"), new string('b', 64));
        YtDlpProvisioner provisioner = CreateProvisioner(manifest, locator);

        YtDlpInfo? result = await provisioner.EnsureAsync();

        result.Should().BeSameAs(existing);
        File.Exists(Path.Combine(_vendorDir, ExecutableName)).Should().BeFalse();
    }

    [Fact]
    public async Task EnsureAsync_ReturnsNull_WhenNoSourceForPlatform()
    {
        IYtDlpLocator locator = Substitute.For<IYtDlpLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns((YtDlpInfo?)null);

        var manifest = new YtDlpManifest([]); // nothing pinned for any platform
        YtDlpProvisioner provisioner = CreateProvisioner(manifest, locator);

        YtDlpInfo? result = await provisioner.EnsureAsync();

        result.Should().BeNull("the caller should then surface a 'no build for this platform' message");
    }

    private YtDlpProvisioner CreateProvisioner(YtDlpManifest manifest, IYtDlpLocator locator)
    {
        var options = new YtDlpOptions { VendorDirectory = _vendorDir };
        IAppInfoProvider appInfo = Substitute.For<IAppInfoProvider>();
        appInfo.Name.Returns("JustDownloadTest");

        return new YtDlpProvisioner(
            locator,
            options,
            manifest,
            new ChecksumVerifier(),
            new TestHandlerProvider(),
            appInfo,
            NullLogger<YtDlpProvisioner>.Instance);
    }

    private static YtDlpManifest ManifestFor(Uri url, string sha256) =>
        new([new YtDlpDownloadSource(FfmpegManifest.CurrentRuntimeIdentifier, "test", url, sha256)]);

    /// <summary>Builds a small fake yt-dlp binary and its SHA-256.</summary>
    private static (byte[] Binary, string Sha256) FakeYtDlpBinary()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("fake-yt-dlp-binary");
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return (bytes, hash);
    }

    private sealed class TestHandlerProvider : ISharedHttpHandlerProvider
    {
        public SocketsHttpHandler Handler { get; } = new();

        public void Dispose() => Handler.Dispose();
    }
}
