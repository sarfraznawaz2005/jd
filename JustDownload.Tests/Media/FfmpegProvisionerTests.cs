using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
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
/// Download-on-first-use provisioning of a pinned LGPL ffmpeg (TASK-079). Drives the provisioner against a
/// loopback server serving a fake ffmpeg archive so the download → SHA-256 verify → extract flow is tested
/// offline and deterministically, plus the LGPL-only policy guard, integrity rejection, idempotence, and
/// the graceful "no source for this platform" path.
/// </summary>
public sealed class FfmpegProvisionerTests : IDisposable
{
    private static readonly string ExecutableName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

    private readonly string _vendorDir = Path.Combine(
        Path.GetTempPath(), "jd-ffmpeg-test-" + Guid.NewGuid().ToString("N"));

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
    public async Task EnsureAsync_DownloadsVerifiesAndExtracts_WhenAbsent()
    {
        (byte[] archive, string sha256) = BuildFakeFfmpegArchive();
        await using var server = new LoopbackHttpServer { Body = archive, ContentType = "application/zip" };
        var manifest = ManifestFor(server.Url("ffmpeg.zip"), sha256, FfmpegBuildLicense.Lgpl);

        string exePath = Path.Combine(_vendorDir, ExecutableName);
        IFfmpegLocator locator = Substitute.For<IFfmpegLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(
            _ => File.Exists(exePath) ? new FfmpegInfo(exePath, "9.9.9-lgpl") : null);

        FfmpegProvisioner provisioner = CreateProvisioner(manifest, locator);

        FfmpegInfo? result = await provisioner.EnsureAsync();

        result.Should().NotBeNull();
        result!.ExecutablePath.Should().Be(exePath);
        File.Exists(exePath).Should().BeTrue("the executable is extracted from the archive's bin/ folder");
        File.Exists(Path.Combine(_vendorDir, "avcodec-extra.dll"))
            .Should().BeTrue("side-by-side libraries are extracted too");
        File.Exists(Path.Combine(_vendorDir, ".ffmpeg-download.partial"))
            .Should().BeFalse("the temp archive is removed after extraction");
    }

    [Fact]
    public async Task EnsureAsync_Throws_AndLeavesNothing_OnChecksumMismatch()
    {
        (byte[] archive, _) = BuildFakeFfmpegArchive();
        await using var server = new LoopbackHttpServer { Body = archive };
        var manifest = ManifestFor(server.Url("ffmpeg.zip"), new string('a', 64), FfmpegBuildLicense.Lgpl);

        IFfmpegLocator locator = Substitute.For<IFfmpegLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns((FfmpegInfo?)null);

        FfmpegProvisioner provisioner = CreateProvisioner(manifest, locator);

        Func<Task> act = async () => await provisioner.EnsureAsync();

        await act.Should().ThrowAsync<FfmpegException>().WithMessage("*integrity check*");
        File.Exists(Path.Combine(_vendorDir, ExecutableName))
            .Should().BeFalse("a corrupt download must not be extracted");
        File.Exists(Path.Combine(_vendorDir, ".ffmpeg-download.partial"))
            .Should().BeFalse("the rejected temp archive is cleaned up");
    }

    [Fact]
    public async Task EnsureAsync_RefusesNonLgplSource()
    {
        (byte[] archive, string sha256) = BuildFakeFfmpegArchive();
        await using var server = new LoopbackHttpServer { Body = archive };
        var manifest = ManifestFor(server.Url("ffmpeg.zip"), sha256, FfmpegBuildLicense.Gpl);

        IFfmpegLocator locator = Substitute.For<IFfmpegLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns((FfmpegInfo?)null);

        FfmpegProvisioner provisioner = CreateProvisioner(manifest, locator);

        Func<Task> act = async () => await provisioner.EnsureAsync();

        await act.Should().ThrowAsync<FfmpegException>().WithMessage("*non-LGPL*");
        File.Exists(Path.Combine(_vendorDir, ExecutableName)).Should().BeFalse();
    }

    [Fact]
    public async Task EnsureAsync_ReturnsExisting_WithoutDownloading()
    {
        var existing = new FfmpegInfo("/usr/bin/ffmpeg", "7.1");
        IFfmpegLocator locator = Substitute.For<IFfmpegLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(existing);

        // An unreachable URL would fail if a download were attempted; it must not be.
        var manifest = ManifestFor(new Uri("http://127.0.0.1:1/never.zip"), new string('b', 64), FfmpegBuildLicense.Lgpl);
        FfmpegProvisioner provisioner = CreateProvisioner(manifest, locator);

        FfmpegInfo? result = await provisioner.EnsureAsync();

        result.Should().BeSameAs(existing);
        File.Exists(Path.Combine(_vendorDir, ExecutableName)).Should().BeFalse();
    }

    [Fact]
    public async Task EnsureAsync_ReturnsNull_WhenNoSourceForPlatform()
    {
        IFfmpegLocator locator = Substitute.For<IFfmpegLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns((FfmpegInfo?)null);

        var manifest = new FfmpegManifest([]); // nothing pinned for any platform
        FfmpegProvisioner provisioner = CreateProvisioner(manifest, locator);

        FfmpegInfo? result = await provisioner.EnsureAsync();

        result.Should().BeNull("the caller should then surface a 'please install ffmpeg' message");
    }

    private FfmpegProvisioner CreateProvisioner(FfmpegManifest manifest, IFfmpegLocator locator)
    {
        var options = new FfmpegOptions { VendorDirectory = _vendorDir };
        IAppInfoProvider appInfo = Substitute.For<IAppInfoProvider>();
        appInfo.Name.Returns("JustDownloadTest");

        return new FfmpegProvisioner(
            locator,
            options,
            manifest,
            new ChecksumVerifier(),
            new TestHandlerProvider(),
            appInfo,
            NullLogger<FfmpegProvisioner>.Instance);
    }

    private static FfmpegManifest ManifestFor(Uri url, string sha256, FfmpegBuildLicense license) =>
        new(
        [
            new FfmpegDownloadSource(
                FfmpegManifest.CurrentRuntimeIdentifier, "test", license, url, sha256),
        ]);

    /// <summary>Builds a minimal zip with a <c>bin/</c> folder holding a fake ffmpeg + library, and its hash.</summary>
    private static (byte[] Archive, string Sha256) BuildFakeFfmpegArchive()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, $"ffmpeg-build/bin/{ExecutableName}", "fake-ffmpeg-binary");
            WriteEntry(zip, "ffmpeg-build/bin/avcodec-extra.dll", "fake-library");
            WriteEntry(zip, "ffmpeg-build/README.txt", "should-not-be-extracted");
        }

        byte[] bytes = buffer.ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return (bytes, hash);
    }

    private static void WriteEntry(ZipArchive zip, string path, string content)
    {
        ZipArchiveEntry entry = zip.CreateEntry(path);
        using StreamWriter writer = new(entry.Open());
        writer.Write(content);
    }

    private sealed class TestHandlerProvider : ISharedHttpHandlerProvider
    {
        public SocketsHttpHandler Handler { get; } = new();

        public void Dispose() => Handler.Dispose();
    }
}
